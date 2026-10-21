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

            long fileLength = 0;
            if (latestFile != null)
            {
                try { fileLength = new FileInfo(latestFile).Length; }
                catch { /* 文件可能正被轮转 */ }
            }

            long bytesRead = 0;
            int fallbackTimestampCount = 0;
            long openMs = 0, readLinesMs = 0, parseMs = 0;
            var readTimer = Stopwatch.StartNew();
            var allData = latestFile != null
                ? ParseFileFromEnd(latestFile, count, cancellationToken,
                    out bytesRead, out fallbackTimestampCount,
                    out openMs, out readLinesMs, out parseMs)
                : new List<CrimpData>();
            readTimer.Stop();

            string fileLockedTag = (latestFile != null && allData.Count == 0 && bytesRead == 0)
                ? " | ⚠ 文件无法打开"
                : "";

            LastReadDiagnostics = latestFile == null
                ? $"定位 {locateTimer.ElapsedMilliseconds} ms | 未找到 CSV"
                : $"定位 {locateTimer.ElapsedMilliseconds} ms | 尾读/解析 {readTimer.ElapsedMilliseconds} ms (打开={openMs} 倒读={readLinesMs} 解析={parseMs}) | 文件 {fileLength / 1024.0 / 1024.0:F1} MB | 实读 {bytesRead / 1024.0:F1} KB{fileLockedTag} | {Path.GetFileName(latestFile)}";

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
    /// 从文件末尾向前读取，使用 Win32 CreateFile + RandomAccess API。
    /// 不触发 oplock，相机写入时也能即时读取。
    /// </summary>
    private static List<CrimpData> ParseFileFromEnd(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken,
        out long bytesRead,
        out int fallbackTimestampCount,
        out long openMs,
        out long readLinesMs,
        out long parseMs)
    {
        bytesRead = 0;
        fallbackTimestampCount = 0;
        openMs = 0;
        readLinesMs = 0;
        parseMs = 0;
        var reversed = new List<CrimpData>(maxCount);
        DateTime? fallbackRecordedAt = null;

        try
        {
            var openTimer = Stopwatch.StartNew();
            using var handle = OpenFileNoOplock(filePath);
            openTimer.Stop();
            openMs = openTimer.ElapsedMilliseconds;

            if (handle == null || handle.IsInvalid)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CSV] 无法打开文件 ({openMs}ms): {Path.GetFileName(filePath)}");
                return reversed;
            }

            if (!GetFileSizeEx(handle, out long fileLength) || fileLength == 0)
                return reversed;

            const int tailBuffer = 16;
            var readTimer2 = Stopwatch.StartNew();
            var lines = ReadLinesFromEnd(handle, fileLength, maxCount + tailBuffer, cancellationToken, out bytesRead);
            readTimer2.Stop();
            readLinesMs = readTimer2.ElapsedMilliseconds;

            if (lines.Count > 0 && !EndsWithLineTerminator(handle, fileLength))
                lines.RemoveAt(0);

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

            System.Diagnostics.Debug.WriteLine(
                $"[CSV 诊断] 打开={openMs}ms | 倒读={readLinesMs}ms | 解析={parseMs}ms | 行数={lines.Count} | 有效={reversed.Count}");
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] IO异常: {Path.GetFileName(filePath)}: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 读取失败 {Path.GetFileName(filePath)}: {ex.Message}");
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// 基于 SafeFileHandle + RandomAccess 从文件末尾向前逐行读取。
    /// 不构造 FileStream，不触发 oplock，相机写入时也能即时读取。
    /// </summary>
    private static List<string> ReadLinesFromEnd(
        SafeFileHandle handle,
        long fileLength,
        int maxLines,
        CancellationToken cancellationToken,
        out long bytesRead)
    {
        bytesRead = 0;
        var lines = new List<string>();
        if (maxLines <= 0 || fileLength == 0) return lines;

        const int bufferSize = 8192;
        var buffer = new byte[bufferSize];
        long position = fileLength;
        var pending = new List<byte>();

        while (position > 0 && lines.Count < maxLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int readSize = (int)Math.Min(bufferSize, position);
            position -= readSize;

            int actualRead = RandomAccess.Read(handle, buffer.AsSpan(0, readSize), position);
            bytesRead += actualRead;
            if (actualRead == 0) break;

            for (int i = actualRead - 1; i >= 0; i--)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    lines.Add(DecodeReversed(pending));
                    pending.Clear();
                    if (lines.Count >= maxLines)
                        break;
                }
                else if (b != (byte)'\r')
                {
                    pending.Add(b);
                }
            }
        }

        return lines;
    }

    private static string DecodeReversed(List<byte> reversedBytes)
    {
        if (reversedBytes.Count == 0) return string.Empty;
        var bytes = new byte[reversedBytes.Count];
        int n = reversedBytes.Count;
        for (int i = 0; i < n; i++)
            bytes[i] = reversedBytes[n - 1 - i];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static bool EndsWithLineTerminator(SafeFileHandle handle, long fileLength)
    {
        if (fileLength == 0) return false;
        var buf = new byte[1];
        int read = RandomAccess.Read(handle, buf.AsSpan(), fileLength - 1);
        return read == 1 && buf[0] is (byte)'\r' or (byte)'\n';
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
