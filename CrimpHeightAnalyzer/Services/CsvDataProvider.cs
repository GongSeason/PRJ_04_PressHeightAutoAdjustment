using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using CrimpHeight.Core.Interfaces;
using CrimpHeight.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// 单次 CSV 读取的结构化耗时与文件定位指标。
/// </summary>
public sealed record CsvReadMetrics(
    long LocateDirMs,
    long EnumCsvMs,
    int CsvCount,
    long OpenMs,
    long SizeMs,
    long ReadMs,
    long ParseMs,
    long BytesRead,
    bool FileChanged,
    bool Rescan,
    bool UsedFallbackTs)
{
    public static CsvReadMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, false, false, false);
}

/// <summary>
/// CSV 数据源 — 从文件系统读取压接数据。
///
/// 核心设计：句柄常驻。
/// 相机软件每 0.1s 写入 CSV，Windows 在每次 CreateFile 时都要做 oplock 协商，
/// 导致打开文件阻塞 2~16 秒。解决方案是只打开一次文件，保持句柄常驻，
/// 每次刷新只做 GetFileSizeEx + RandomAccess.Read，不再重复打开/关闭。
/// </summary>
public class CsvDataProvider : IDataProvider
{
    private readonly string _basePath;
    private readonly int _scanDays;
    private readonly bool _csvBackgroundIoMode;
    private readonly bool _cachedFileLocate;
    private readonly int _newFileMaxLatencySeconds;
    private readonly int _refreshIntervalSeconds;
    private string? _cachedLatestFile;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private DateTime _scanDate = DateTime.MinValue;

    // 句柄常驻：只打开一次，后续刷新复用
    private SafeFileHandle? _cachedHandle;
    private string? _cachedFilePath;
    private readonly object _handleLock = new();

    public DateTime? LastDataTimestamp { get; private set; }
    public string LastReadDiagnostics { get; private set; } = "尚未读取";
    public CsvReadMetrics LastReadMetrics { get; private set; } = CsvReadMetrics.Empty;
    private string? _lastMetricsFilePath;

    /// <summary>实际扫描的根目录路径（规范化后）。</summary>
    public string BasePath => _basePath;

    /// <summary>最近一次成功读取的 CSV 文件全路径；未找到文件时为 null。</summary>
    public string? LastUsedFilePath { get; private set; }

    public CsvDataProvider(
        string basePath,
        int scanDays = 0,
        bool csvBackgroundIoMode = true,
        bool cachedFileLocate = true,
        int newFileMaxLatencySeconds = 60,
        int refreshIntervalSeconds = 10)
    {
        _basePath = basePath;
        _scanDays = scanDays;
        _csvBackgroundIoMode = csvBackgroundIoMode;
        _cachedFileLocate = cachedFileLocate;
        _newFileMaxLatencySeconds = newFileMaxLatencySeconds;
        _refreshIntervalSeconds = Math.Max(1, refreshIntervalSeconds);
    }

    public async Task<List<CrimpData>> GetLatestDataAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "读取条数必须大于 0。");

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool backgroundModeStarted = _csvBackgroundIoMode
                && SetCurrentThreadBackgroundMode(THREAD_MODE_BACKGROUND_BEGIN);
            try
            {
            bool rescan = ShouldRescan(DateTime.UtcNow);
            long locateDirMs = 0, enumCsvMs = 0;
            int csvCount = 0;
            string? latestFile = rescan
                ? ScanLatestCsvFile(out locateDirMs, out enumCsvMs, out csvCount)
                : _cachedLatestFile;

            long fileLength = 0;
            long bytesRead = 0;
            int fallbackTimestampCount = 0;
            long openMs = 0, sizeMs = 0, readMs = 0, parseMs = 0;
            List<CrimpData> allData = new();

            var readTimer = Stopwatch.StartNew();
            if (latestFile != null)
            {
                allData = ReadTailAndParse(latestFile, count, cancellationToken,
                    out bytesRead, out fallbackTimestampCount,
                    out openMs, out sizeMs, out readMs, out parseMs, out fileLength);

                // The cached file disappeared between refreshes. Rescan and retry once in this refresh.
                if (_cachedFileLocate && fileLength < 0)
                {
                    rescan = true;
                    latestFile = ScanLatestCsvFile(out long retryLocateMs, out long retryEnumMs, out int retryCsvCount);
                    locateDirMs += retryLocateMs;
                    enumCsvMs += retryEnumMs;
                    csvCount += retryCsvCount;

                    if (latestFile != null)
                    {
                        allData = ReadTailAndParse(latestFile, count, cancellationToken,
                            out long retryBytesRead, out int retryFallbackCount,
                            out long retryOpenMs, out long retrySizeMs, out long retryReadMs,
                            out long retryParseMs, out long retryFileLength);
                        bytesRead = retryBytesRead;
                        fallbackTimestampCount = retryFallbackCount;
                        openMs += retryOpenMs;
                        sizeMs += retrySizeMs;
                        readMs += retryReadMs;
                        parseMs += retryParseMs;
                        fileLength = retryFileLength;
                    }
                }
            }
            readTimer.Stop();

            bool fileChanged = !string.Equals(_lastMetricsFilePath, latestFile, StringComparison.OrdinalIgnoreCase);
            _lastMetricsFilePath = latestFile;
            LastUsedFilePath = latestFile;

            long outerMs = readTimer.ElapsedMilliseconds;
            long innerSum = openMs + sizeMs + readMs + parseMs;
            string scheduleTag = (outerMs - innerSum) > 100
                ? $" | ⚠调度延迟={outerMs - innerSum}ms"
                : "";

            LastReadDiagnostics = latestFile == null
                ? $"定位 {locateDirMs + enumCsvMs} ms | 未找到 CSV"
                : $"定位 {locateDirMs + enumCsvMs} ms | 总计 {outerMs} ms (打开={openMs} 读取={sizeMs + readMs} 解析={parseMs}) | 文件 {fileLength / 1024.0 / 1024.0:F1} MB | 实读 {bytesRead / 1024.0:F1} KB{scheduleTag} | {Path.GetFileName(latestFile)}";

            LastReadMetrics = new CsvReadMetrics(
                locateDirMs, enumCsvMs, csvCount, openMs, sizeMs, readMs, parseMs,
                bytesRead, fileChanged, rescan, UsedFallbackTs: fallbackTimestampCount > 0);

            if (allData.Count > 0)
                LastDataTimestamp = allData[^1].RecordedAt;

            return allData;
            }
            finally
            {
                if (backgroundModeStarted)
                    SetCurrentThreadBackgroundMode(THREAD_MODE_BACKGROUND_END);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 核心方法：获取或复用文件句柄 → 读尾部字节 → 内存中解析。
    /// 句柄只在首次或文件切换时打开，后续刷新直接复用，不再触发 oplock。
    /// </summary>
    private List<CrimpData> ReadTailAndParse(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken,
        out long bytesRead,
        out int fallbackTimestampCount,
        out long openMs,
        out long sizeMs,
        out long readMs,
        out long parseMs,
        out long fileLength)
    {
        bytesRead = 0;
        fallbackTimestampCount = 0;
        openMs = 0;
        sizeMs = 0;
        readMs = 0;
        parseMs = 0;
        fileLength = 0;
        var reversed = new List<CrimpData>(maxCount);
        DateTime? fallbackRecordedAt = null;
        DateTime fileLastWriteTimeLocal = DateTime.MinValue;

        try
        {
            // 第一步：获取或复用句柄（只有首次/文件切换时才真正打开）
            var openTimer = Stopwatch.StartNew();
            var handle = GetOrOpenHandle(filePath);
            openTimer.Stop();
            openMs = openTimer.ElapsedMilliseconds;

            if (handle == null || handle.IsInvalid || handle.IsClosed)
            {
                var missingCheckTimer = Stopwatch.StartNew();
                fileLength = GetRealFileSize(filePath, out _);
                missingCheckTimer.Stop();
                sizeMs = missingCheckTimer.ElapsedMilliseconds;
                System.Diagnostics.Debug.WriteLine(
                    $"[CSV] 无法获取句柄 ({openMs}ms): {Path.GetFileName(filePath)}");
                return reversed;
            }

            // 第二步：取实时文件大小并读取尾部字节。
            // 用路径查实时文件大小，不用句柄查（句柄常驻时 GetFileSizeEx 可能返回缓存旧值）。
            var sizeTimer = Stopwatch.StartNew();
            fileLength = GetRealFileSize(filePath, out fileLastWriteTimeLocal);
            sizeTimer.Stop();
            sizeMs = sizeTimer.ElapsedMilliseconds;
            if (fileLength <= 0)
            {
                InvalidateCachedHandle();
                return reversed;
            }

            const int tailBuffer = 16;
            int totalLines = maxCount + tailBuffer;
            int tailBytes = totalLines * 200;

            long readStart = Math.Max(0, fileLength - tailBytes);
            int readSize = (int)(fileLength - readStart);
            var buffer = new byte[readSize];

            var readTimer2 = Stopwatch.StartNew();
            int actualRead;
            try
            {
                actualRead = RandomAccess.Read(handle, buffer.AsSpan(0, readSize), readStart);
            }
            catch (IOException)
            {
                // 读取失败（句柄可能被相机侧关闭/文件被轮转），清除缓存下次重新打开
                InvalidateCachedHandle();
                System.Diagnostics.Debug.WriteLine(
                    $"[CSV] 读取失败，句柄已失效，下次重新打开: {Path.GetFileName(filePath)}");
                return reversed;
            }

            readTimer2.Stop();
            readMs = readTimer2.ElapsedMilliseconds;
            bytesRead = actualRead;

            if (actualRead == 0)
                return reversed;

            // 第三步：纯内存解析（不持有任何文件锁，不涉及 I/O）
            var parseTimer = Stopwatch.StartNew();

            byte[] data = actualRead < readSize ? buffer[..actualRead] : buffer;
            bool endsWithNewline = data[^1] is (byte)'\n' or (byte)'\r';
            var lines = ExtractLinesFromTail(data, totalLines, endsWithNewline);

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reversed.Count >= maxCount) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var record = ParseLine(line, filePath, ref fallbackRecordedAt, fileLastWriteTimeLocal, out bool usedFallback);
                if (usedFallback) fallbackTimestampCount++;
                if (record != null) reversed.Add(record);
            }

            parseTimer.Stop();
            parseMs = parseTimer.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 异常: {Path.GetFileName(filePath)}: {ex.Message}");
        }

        reversed.Reverse();
        return reversed;
    }

    #region 句柄常驻管理

    /// <summary>
    /// 获取或复用文件句柄。只有以下情况才真正调用 CreateFile：
    /// 1. 首次打开（_cachedHandle == null）
    /// 2. 文件路径变了（日期切换，新的 CSV 文件）
    /// 3. 上次句柄已失效（被 InvalidateCachedHandle 清除）
    /// 其他情况直接返回缓存的句柄，耗时 0ms。
    /// </summary>
    private SafeFileHandle? GetOrOpenHandle(string filePath)
    {
        lock (_handleLock)
        {
            // 路径没变且句柄有效，直接复用
            if (_cachedHandle != null && !_cachedHandle.IsInvalid && !_cachedHandle.IsClosed
                && _cachedFilePath == filePath)
            {
                return _cachedHandle;
            }

            // 需要打开新句柄：先关闭旧的（后台关闭，不阻塞）
            if (_cachedHandle != null && !_cachedHandle.IsInvalid && !_cachedHandle.IsClosed)
            {
                CloseHandleInBackground(_cachedHandle);
            }

            _cachedHandle = null;
            _cachedFilePath = null;

            var handle = OpenFileNoOplock(filePath);
            if (handle != null && !handle.IsInvalid)
            {
                _cachedHandle = handle;
                _cachedFilePath = filePath;
                System.Diagnostics.Debug.WriteLine(
                    $"[CSV] 句柄已打开并缓存: {Path.GetFileName(filePath)}");
            }

            return _cachedHandle;
        }
    }

    /// <summary>
    /// 使缓存的句柄失效（读取失败时调用），下次 GetOrOpenHandle 会重新打开。
    /// </summary>
    private void InvalidateCachedHandle()
    {
        lock (_handleLock)
        {
            if (_cachedHandle != null && !_cachedHandle.IsInvalid && !_cachedHandle.IsClosed)
            {
                CloseHandleInBackground(_cachedHandle);
            }
            _cachedHandle = null;
            _cachedFilePath = null;
        }
    }

    #endregion

    #region Win32 原生文件打开

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetFileAttributesEx(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FILE_ATTRIBUTE_DATA fileData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    /// <summary>
    /// 通过文件路径获取实时文件大小，不依赖已有句柄。
    /// 句柄常驻时 GetFileSizeEx 可能返回缓存的旧值，
    /// 而 GetFileAttributesEx 每次都查询文件系统元数据，能看到相机最新写入后的真实大小。
    /// </summary>
    private static long GetRealFileSize(string filePath, out DateTime lastWriteTimeLocal)
    {
        lastWriteTimeLocal = DateTime.MinValue;
        if (GetFileAttributesEx(filePath, 0, out var data))
        {
            long fileTime = ((long)(uint)data.ftLastWriteTime.dwHighDateTime << 32)
                          | (uint)data.ftLastWriteTime.dwLowDateTime;
            lastWriteTimeLocal = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            return ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
        }
        return -1;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    private static bool SetCurrentThreadBackgroundMode(int mode)
    {
        try
        {
            if (SetThreadPriority(GetCurrentThread(), mode))
                return true;

            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"[CSV] 线程后台 I/O 模式调用失败 ({mode:X8}): {error}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 线程后台 I/O 模式异常 ({mode:X8}): {ex.Message}");
        }

        return false;
    }

    private static SafeFileHandle? OpenFileNoOplock(string filePath)
    {
        var handle = CreateFile(
            filePath,
            GENERIC_READ,
            FILE_SHARE_READ_WRITE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_RANDOM_ACCESS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine(
                $"[CSV] CreateFile 失败: Win32 错误={error} ({new Win32Exception(error).Message})");
            return null;
        }

        return handle;
    }

    private static int _pendingCloses;
    private const int MaxPendingCloses = 5;

    private static void CloseHandleInBackground(SafeFileHandle handle)
    {
        int pending = Interlocked.Increment(ref _pendingCloses);
        if (pending > MaxPendingCloses)
        {
            Interlocked.Decrement(ref _pendingCloses);
            try { handle.Close(); } catch { }
            return;
        }

        Task.Run(() =>
        {
            try { handle.Close(); }
            catch { }
            finally { Interlocked.Decrement(ref _pendingCloses); }
        });
    }

    #endregion

    #region 内存中解析

    private static List<string> ExtractLinesFromTail(byte[] data, int maxLines, bool endsWithNewline)
    {
        var lines = new List<string>();
        if (data.Length == 0 || maxLines <= 0) return lines;

        int end = data.Length;

        if (!endsWithNewline)
        {
            while (end > 0 && data[end - 1] != (byte)'\n')
                end--;
        }

        while (end > 0 && lines.Count < maxLines)
        {
            while (end > 0 && (data[end - 1] == (byte)'\n' || data[end - 1] == (byte)'\r'))
                end--;
            if (end == 0) break;

            int lineEnd = end;
            while (end > 0 && data[end - 1] != (byte)'\n')
                end--;

            int lineStart = end;
            lines.Add(System.Text.Encoding.UTF8.GetString(data, lineStart, lineEnd - lineStart));
        }

        return lines;
    }

    private static CrimpData? ParseLine(
        string line, string filePath,
        ref DateTime? fallbackRecordedAt, DateTime fileLastWriteTimeLocal,
        out bool usedFallbackTimestamp)
    {
        usedFallbackTimestamp = false;
        var parts = line.Split(',');
        if (parts.Length < 7) return null;

        DateTime? recordedAt = ParseTimestamp(parts[0], filePath);
        if (!recordedAt.HasValue)
        {
            fallbackRecordedAt ??= fileLastWriteTimeLocal;
            recordedAt = fallbackRecordedAt;
            usedFallbackTimestamp = true;
        }

        return new CrimpData
        {
            Timestamp = parts[0].Trim(),
            RecordedAt = recordedAt.Value,
            Head1Height = ParseDouble(parts[1]),
            Head2Height = ParseDouble(parts[3])
        };
    }

    private static double ParseDouble(string s)
    {
        if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value))
            return value;
        return 0;
    }

    private static DateTime? ParseTimestamp(string s, string filePath)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string value = s.Trim();

        string[] dateTimeFormats = {
            "yyyyMMddHHmmssfff", "yyyyMMddHHmmss",
            "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff",
            "yyyyMMdd HHmmss",
        };

        if (DateTime.TryParseExact(value, dateTimeFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            string digits = decimal.Truncate(numeric).ToString("0", CultureInfo.InvariantCulture);
            if (DateTime.TryParseExact(digits,
                    new[] { "yyyyMMddHHmmssfff", "yyyyMMddHHmmss" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return dt;

            try
            {
                if (numeric >= 1_000_000_000m && numeric < 253_402_300_800m)
                    return DateTimeOffset.FromUnixTimeSeconds(decimal.ToInt64(numeric)).LocalDateTime;
                if (numeric >= 100_000_000_000m && numeric < 253_402_300_800_000m)
                    return DateTimeOffset.FromUnixTimeMilliseconds(decimal.ToInt64(numeric)).LocalDateTime;
            }
            catch (ArgumentOutOfRangeException) { }
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;

        string[] timeFormats = { "HH-mm-ss-fff", "HH-mm-ss" };
        if (DateTime.TryParseExact(value, timeFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            && TryGetFileDate(filePath, out var fileDate))
            return fileDate.Add(time.TimeOfDay);

        return null;
    }

    #endregion

    #region 文件定位

    private bool ShouldRescan(DateTime nowUtc)
    {
        if (!_cachedFileLocate || _cachedLatestFile == null || _scanDate != DateTime.Today)
            return true;

        long intervalSeconds = Math.Max(0L, (long)_newFileMaxLatencySeconds - _refreshIntervalSeconds);
        return nowUtc - _lastScanUtc >= TimeSpan.FromSeconds(intervalSeconds);
    }

    private string? ScanLatestCsvFile(out long locateDirMs, out long enumCsvMs, out int csvCount)
    {
        var file = GetLatestCsvFile(out locateDirMs, out enumCsvMs, out csvCount);
        _cachedLatestFile = file;
        _lastScanUtc = DateTime.UtcNow;
        _scanDate = DateTime.Today;
        return file;
    }

    private string? GetLatestCsvFile(out long locateDirMs, out long enumCsvMs, out int csvCount)
    {
        locateDirMs = 0;
        enumCsvMs = 0;
        csvCount = 0;

        var locateTimer = Stopwatch.StartNew();
        if (!Directory.Exists(_basePath))
        {
            locateTimer.Stop();
            locateDirMs = locateTimer.ElapsedMilliseconds;
            return null;
        }

        var cutoff = _scanDays > 0 ? DateTime.Today.AddDays(-_scanDays) : DateTime.MinValue;
        var latestDateDir = FindLatestDateDirectory(_basePath, cutoff);
        locateTimer.Stop();
        locateDirMs = locateTimer.ElapsedMilliseconds;
        if (latestDateDir == null) return null;

        var enumTimer = Stopwatch.StartNew();
        var latestFile = FindCsvInDirectory(latestDateDir, out csvCount);
        enumTimer.Stop();
        enumCsvMs = enumTimer.ElapsedMilliseconds;
        return latestFile;
    }

    private static string? FindLatestDateDirectory(string basePath, DateTime cutoff)
    {
        var latest = SelectLatestDateDir(EnumerateDirectoriesSafe(basePath), cutoff);
        if (latest != null) return latest;

        foreach (var sub in EnumerateDirectoriesSafe(basePath))
        {
            var hit = SelectLatestDateDir(EnumerateDirectoriesSafe(sub), cutoff);
            if (hit != null) return hit;
        }
        return null;
    }

    private static string? SelectLatestDateDir(IEnumerable<string> directories, DateTime cutoff) =>
        directories
            .Where(dir => TryParseDirDate(dir, out var date) && date >= cutoff)
            .OrderByDescending(GetDirDateSafe)
            .FirstOrDefault();

    private static string? FindCsvInDirectory(string directory, out int csvCount)
    {
        csvCount = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        try
        {
            var files = Directory.EnumerateFiles(directory, "*.csv", options).ToList();
            csvCount = files.Count;
            return files
                .OrderByDescending(GetFileDateSafe)
                .ThenByDescending(GetLastWriteTimeUtcSafe)
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        try { return Directory.EnumerateDirectories(path, "*", options); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static bool TryParseDirDate(string dirPath, out DateTime date)
    {
        string name = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return DateTime.TryParseExact(name, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static DateTime GetDirDateSafe(string dirPath) =>
        TryParseDirDate(dirPath, out var date) ? date : DateTime.MinValue;

    private static bool TryGetFileDate(string filePath, out DateTime date)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        int separator = fileName.LastIndexOf('_');
        string datePart = separator >= 0 ? fileName[(separator + 1)..] : fileName;
        return DateTime.TryParseExact(datePart, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static DateTime GetLastWriteTimeUtcSafe(string filePath)
    {
        try { return File.GetLastWriteTimeUtc(filePath); }
        catch { return DateTime.MinValue; }
    }

    private static DateTime GetFileDateSafe(string filePath) =>
        TryGetFileDate(filePath, out var date) ? date : DateTime.MinValue;

    #endregion
}
