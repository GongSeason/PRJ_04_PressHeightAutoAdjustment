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
/// CSV 数据源 — 从文件系统读取压接数据
/// 合并了原 FileScannerService + CsvParserService
/// </summary>
public class CsvDataProvider : IDataProvider
{
    private readonly string _basePath;
    private readonly int _scanDays;

    public DateTime? LastDataTimestamp { get; private set; }
    public string LastReadDiagnostics { get; private set; } = "尚未读取";

    public CsvDataProvider(string basePath, int scanDays = 0)
    {
        _basePath = basePath;
        _scanDays = scanDays;
    }

    public async Task<List<CrimpData>> GetLatestDataAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "读取条数必须大于 0。");

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var locateTimer = Stopwatch.StartNew();
            var latestFile = GetLatestCsvFile();
            locateTimer.Stop();

            // 注意：不再在这里用 FileInfo 取文件大小（它也会触发 oplock）。
            // 文件大小在 ParseFileFromEnd 内部通过 Win32 GetFileSizeEx 获取。
            long fileLength = 0;

            long bytesRead = 0;
            int fallbackTimestampCount = 0;
            long openMs = 0, readLinesMs = 0, parseMs = 0;
            var readTimer = Stopwatch.StartNew();
            var allData = latestFile != null
                ? ParseFileFromEnd(latestFile, count, cancellationToken,
                    out bytesRead, out fallbackTimestampCount,
                    out openMs, out readLinesMs, out parseMs,
                    out fileLength)
                : new List<CrimpData>();
            readTimer.Stop();

            string fileLockedTag = (latestFile != null && allData.Count == 0 && bytesRead == 0)
                ? " | ⚠ 文件无法打开"
                : "";

            // 外层总耗时 vs 内部子项之和，差值即为 Task.Run 调度延迟或 GC 暂停
            long outerMs = readTimer.ElapsedMilliseconds;
            long innerSum = openMs + readLinesMs + parseMs;
            string scheduleTag = (outerMs - innerSum) > 100
                ? $" | ⚠调度延迟={outerMs - innerSum}ms"
                : "";

            LastReadDiagnostics = latestFile == null
                ? $"定位 {locateTimer.ElapsedMilliseconds} ms | 未找到 CSV"
                : $"定位 {locateTimer.ElapsedMilliseconds} ms | 尾读/解析 {outerMs} ms (打开={openMs} 倒读={readLinesMs} 解析={parseMs}) | 文件 {fileLength / 1024.0 / 1024.0:F1} MB | 实读 {bytesRead / 1024.0:F1} KB{fileLockedTag}{scheduleTag} | {Path.GetFileName(latestFile)}";

            if (allData.Count > 0)
                LastDataTimestamp = allData[^1].RecordedAt;

            return allData;
        }, cancellationToken);
    }

    private string? GetLatestCsvFile()
    {
        if (!Directory.Exists(_basePath))
            return null;

        var cutoff = _scanDays > 0 ? DateTime.Today.AddDays(-_scanDays) : DateTime.MinValue;

        var latestDateDir = FindLatestDateDirectory(_basePath, cutoff);
        if (latestDateDir == null)
            return null;

        return FindCsvInDirectory(latestDateDir);
    }

    private static string? FindLatestDateDirectory(string basePath, DateTime cutoff)
    {
        var latest = SelectLatestDateDir(EnumerateDirectoriesSafe(basePath), cutoff);
        if (latest != null)
            return latest;

        foreach (var sub in EnumerateDirectoriesSafe(basePath))
        {
            var hit = SelectLatestDateDir(EnumerateDirectoriesSafe(sub), cutoff);
            if (hit != null)
                return hit;
        }

        return null;
    }

    private static string? SelectLatestDateDir(IEnumerable<string> directories, DateTime cutoff) =>
        directories
            .Where(dir => TryParseDirDate(dir, out var date) && date >= cutoff)
            .OrderByDescending(GetDirDateSafe)
            .FirstOrDefault();

    private static string? FindCsvInDirectory(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        try
        {
            return Directory.EnumerateFiles(directory, "*.csv", options)
                .OrderByDescending(GetFileDateSafe)
                .ThenByDescending(GetLastWriteTimeUtcSafe)
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 枚举 CSV 失败 {directory}: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        try
        {
            return Directory.EnumerateDirectories(path, "*", options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 枚举目录失败 {path}: {ex.Message}");
            return Enumerable.Empty<string>();
        }
    }

    private static bool TryParseDirDate(string dirPath, out DateTime date)
    {
        string name = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return DateTime.TryParseExact(
            name,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static DateTime GetDirDateSafe(string dirPath) =>
        TryParseDirDate(dirPath, out var date) ? date : DateTime.MinValue;

    #region Win32 原生文件打开 — 绕过 .NET FileStream 的 oplock 阻塞

    // 相机软件每 0.1s 写入 CSV，.NET FileStream 构造会触发 Windows oplock 协商，
    // 导致打开文件阻塞 3~12 秒。改用 Win32 CreateFile 以最大共享模式直接打开，
    // 即使相机正在写入也能立即拿到只读句柄，再用 .NET RandomAccess API 读取。

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

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;

    /// <summary>
    /// 用 Win32 CreateFile 打开文件，完全绕过 .NET FileStream 的 oplock 协商。
    /// 以最大共享模式（读+写+删除）打开，即使相机软件正在写入也能立即拿到句柄。
    /// </summary>
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

    #endregion

    /// <summary>
    /// 从文件末尾向前读取。
    /// 策略：用 Win32 CreateFile 打开 → 只读末尾 tailBytes 字节到内存 → 立即关闭句柄 → 在内存中解析。
    /// 关键：把"持有文件句柄"的时间压到最短（只读一次 Read），避免 CloseHandle 时的 oplock 等待。
    /// 如果 CreateFile 本身被阻塞（相机不允许共享读），则回退到 robocopy 快照方式。
    /// </summary>
    private static List<CrimpData> ParseFileFromEnd(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken,
        out long bytesRead,
        out int fallbackTimestampCount,
        out long openMs,
        out long readLinesMs,
        out long parseMs,
        out long fileLength)
    {
        bytesRead = 0;
        fallbackTimestampCount = 0;
        openMs = 0;
        readLinesMs = 0;
        parseMs = 0;
        fileLength = 0;
        var reversed = new List<CrimpData>(maxCount);
        DateTime? fallbackRecordedAt = null;

        try
        {
            // 总计时：覆盖 ParseFileFromEnd 内部全部代码，用于排查"幽灵耗时"
            var totalInner = Stopwatch.StartNew();

            const int tailBuffer = 16;
            int totalLines = maxCount + tailBuffer;
            int tailBytes = totalLines * 200;

            var openTimer = Stopwatch.StartNew();
            byte[]? tailData = ReadTailBytesViaWin32(filePath, tailBytes, out long actualFileLength);
            openTimer.Stop();
            openMs = openTimer.ElapsedMilliseconds;
            fileLength = actualFileLength;

            // Win32 方式失败时（极少见），本次跳过，下个刷新周期重试
            // 不再回退到 File.Copy（复制 6MB 文件本身就慢且同样可能被锁阻塞）

            if (tailData == null || tailData.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CSV] 无法读取文件 ({openMs}ms): {Path.GetFileName(filePath)}");
                return reversed;
            }

            bytesRead = tailData.Length;

            // 第二步：在内存中解析行（不再持有任何文件句柄）
            var readTimer2 = Stopwatch.StartNew();
            bool endsWithNewline = tailData.Length > 0
                && (tailData[^1] == (byte)'\n' || tailData[^1] == (byte)'\r');
            var lines = ExtractLinesFromTail(tailData, totalLines, endsWithNewline);
            readTimer2.Stop();
            readLinesMs = readTimer2.ElapsedMilliseconds;

            var parseTimer = Stopwatch.StartNew();
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reversed.Count >= maxCount) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var data = ParseLine(line, filePath, ref fallbackRecordedAt, out bool usedFallbackTimestamp);
                if (usedFallbackTimestamp)
                    fallbackTimestampCount++;
                if (data != null)
                    reversed.Add(data);
            }
            parseTimer.Stop();
            parseMs = parseTimer.ElapsedMilliseconds;

            totalInner.Stop();
            long innerTotalMs = totalInner.ElapsedMilliseconds;
            long unaccounted = innerTotalMs - openMs - readLinesMs - parseMs;

            System.Diagnostics.Debug.WriteLine(
                $"[CSV 诊断] 内部总计={innerTotalMs}ms | 打开={openMs}ms | 倒读={readLinesMs}ms | 解析={parseMs}ms | 未计入={unaccounted}ms | 行数={lines.Count} | 有效={reversed.Count}");

            // 把未计入的时间归到 openMs，让外层诊断信息能对上
            if (unaccounted > 10)
                openMs += unaccounted;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 读取失败 {Path.GetFileName(filePath)}: {ex.Message}");
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// 用 Win32 CreateFile 打开文件，只读末尾 tailBytes 字节到内存。
    /// 关键：CreateFile 和 Read 都可能快速完成，但 CloseHandle 可能因 oplock break 阻塞数秒。
    /// 所以把 Close 放到后台线程，主线程拿到数据后立即返回。
    /// </summary>
    private static byte[]? ReadTailBytesViaWin32(string filePath, int tailBytes, out long fileLength)
    {
        fileLength = 0;
        SafeFileHandle? handle = null;
        try
        {
            handle = OpenFileNoOplock(filePath);
            if (handle == null || handle.IsInvalid)
                return null;

            if (!GetFileSizeEx(handle, out fileLength) || fileLength == 0)
            {
                CloseHandleInBackground(handle);
                return null;
            }

            long readStart = Math.Max(0, fileLength - tailBytes);
            int readSize = (int)(fileLength - readStart);
            var buffer = new byte[readSize];

            int actualRead = RandomAccess.Read(handle, buffer.AsSpan(), readStart);

            // 关键：Close 可能阻塞数秒（oplock break），放到后台线程不等它
            CloseHandleInBackground(handle);
            handle = null; // 防止 finally 重复关闭

            if (actualRead < readSize)
                return buffer[..actualRead];
            return buffer;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] Win32 读取异常: {ex.Message}");
            if (handle != null && !handle.IsInvalid && !handle.IsClosed)
                CloseHandleInBackground(handle);
            return null;
        }
    }

    /// <summary>
    /// 在后台线程关闭文件句柄，避免 CloseHandle 的 oplock break 等待阻塞主流程。
    /// 安全性说明：
    /// - 句柄是只读 + 最大共享模式打开的，持有期间不影响相机写入。
    /// - 后台线程最终会完成关闭，不会泄漏。
    /// - _pendingCloses 计数器防止极端情况下句柄积压过多。
    /// </summary>
    private static int _pendingCloses;
    private const int MaxPendingCloses = 5;

    private static void CloseHandleInBackground(SafeFileHandle handle)
    {
        int pending = Interlocked.Increment(ref _pendingCloses);
        if (pending > MaxPendingCloses)
        {
            // 积压过多，同步关闭，宁可慢一次也不泄漏
            Interlocked.Decrement(ref _pendingCloses);
            try { handle.Close(); } catch { }
            return;
        }

        Task.Run(() =>
        {
            try { handle.Close(); }
            catch { /* 关闭失败不影响主流程 */ }
            finally { Interlocked.Decrement(ref _pendingCloses); }
        });
    }

    /// <summary>
    /// 从尾部字节数组中提取最后 maxLines 行（从新到旧排列）。
    /// 纯内存操作，不涉及任何文件 I/O。
    /// </summary>
    private static List<string> ExtractLinesFromTail(byte[] data, int maxLines, bool endsWithNewline)
    {
        var lines = new List<string>();
        if (data.Length == 0 || maxLines <= 0) return lines;

        int end = data.Length;

        // 如果末尾不是换行符，最后一段是未完成的行，跳过
        if (!endsWithNewline)
        {
            while (end > 0 && data[end - 1] != (byte)'\n')
                end--;
        }

        // 从后往前扫描换行符，提取每一行
        while (end > 0 && lines.Count < maxLines)
        {
            // 跳过尾部的 \r\n
            while (end > 0 && (data[end - 1] == (byte)'\n' || data[end - 1] == (byte)'\r'))
                end--;

            if (end == 0) break;

            // 找到这一行的起始位置
            int lineEnd = end;
            while (end > 0 && data[end - 1] != (byte)'\n')
                end--;

            int lineStart = end;
            string line = System.Text.Encoding.UTF8.GetString(data, lineStart, lineEnd - lineStart);
            lines.Add(line);
        }

        return lines;
    }

    private static CrimpData? ParseLine(
        string line,
        string filePath,
        ref DateTime? fallbackRecordedAt,
        out bool usedFallbackTimestamp)
    {
        usedFallbackTimestamp = false;
        var parts = line.Split(',');
        if (parts.Length < 7) return null;

        DateTime? recordedAt = ParseTimestamp(parts[0], filePath);
        if (!recordedAt.HasValue)
        {
            fallbackRecordedAt ??= File.GetLastWriteTime(filePath);
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
        if (double.TryParse(
                s.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            && double.IsFinite(value))
            return value;
        return 0;
    }

    private static DateTime? ParseTimestamp(string s, string filePath)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        string value = s.Trim();
        string[] dateTimeFormats = {
            "yyyyMMddHHmmssfff",
            "yyyyMMddHHmmss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fff",
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
}
