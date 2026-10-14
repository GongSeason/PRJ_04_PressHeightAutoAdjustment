using System.Diagnostics;
using System.Globalization;
using System.IO;
using CrimpHeight.Core.Interfaces;
using CrimpHeight.Core.Models;

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

            // 仅选取最新一个 CSV；记录不足时只用已有记录，不再回退到旧文件。
            var locateTimer = Stopwatch.StartNew();
            var latestFile = GetLatestCsvFile();
            locateTimer.Stop();

            long fileLength = 0;
            if (latestFile != null)
            {
                try { fileLength = new FileInfo(latestFile).Length; }
                catch { /* 文件可能正被轮转；诊断信息允许长度未知。 */ }
            }

            long bytesRead = 0;
            var readTimer = Stopwatch.StartNew();
            var allData = latestFile != null
                ? ParseFileFromEnd(latestFile, count, cancellationToken, out bytesRead)
                : new List<CrimpData>();
            readTimer.Stop();

            LastReadDiagnostics = latestFile == null
                ? $"定位 {locateTimer.ElapsedMilliseconds} ms | 未找到 CSV"
                : $"定位 {locateTimer.ElapsedMilliseconds} ms | 尾读/解析 {readTimer.ElapsedMilliseconds} ms | 文件 {fileLength / 1024.0 / 1024.0:F1} MB | 实读 {bytesRead / 1024.0:F1} KB | {Path.GetFileName(latestFile)}";

            LastDataTimestamp = allData.Count > 0 ? allData[^1].RecordedAt : null;

            return allData;
        }, cancellationToken);
    }

    private string? GetLatestCsvFile()
    {
        if (!Directory.Exists(_basePath))
            return null;

        var cutoff = _scanDays > 0 ? DateTime.Today.AddDays(-_scanDays) : DateTime.MinValue;

        // 目录结构固定为 <监视根目录>\<yyyyMMdd 日期文件夹>\Data_*_<日期>.csv，
        // 且日期文件夹里混有上万张 NG 图片。为避免每次刷新都遍历海量图片
        // （尤其在 OneDrive 占位目录下会触发云端水合，极慢），
        // 这里不再全树递归枚举所有 CSV，而是：
        //   1. 只枚举“日期文件夹”本身（目录项数量极少，不碰图片）；
        //   2. 按文件夹名（yyyyMMdd）直接选出最新一天；
        //   3. 仅在该日期文件夹本层查找当天的 CSV。
        var latestDateDir = FindLatestDateDirectory(_basePath, cutoff);
        if (latestDateDir == null)
            return null;

        return FindCsvInDirectory(latestDateDir);
    }

    /// <summary>
    /// 在 <paramref name="basePath"/> 下查找名称为 yyyyMMdd、日期 ≥ cutoff 的最新日期文件夹。
    /// 仅枚举目录项（不触碰图片等文件）。为兼容“F2 之上可能新增父级目录”的情况，
    /// 若本层没有任何日期文件夹，则下探一层再找（最多两层，仍不做全树递归，避免遍历图片）。
    /// </summary>
    private static string? FindLatestDateDirectory(string basePath, DateTime cutoff)
    {
        var latest = SelectLatestDateDir(EnumerateDirectoriesSafe(basePath), cutoff);
        if (latest != null)
            return latest;

        // 本层未命中：逐个下探一层子目录（如 <根>\F2\<日期>），仍只枚举目录。
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

    /// <summary>
    /// 仅在指定日期文件夹本层查找 CSV（通常只有一个 Data_*_&lt;日期&gt;.csv）。
    /// 若存在多个，取文件名日期最新、再按写入时间兜底。只列单层目录，开销很小。
    /// </summary>
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

    /// <summary>
    /// 枚举单层子目录，忽略无法访问的项，不让整次扫描失败。不递归、不触碰文件。
    /// </summary>
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

    /// <summary>
    /// 解析日期文件夹名（yyyyMMdd）。文件夹名即日期，不含前缀。
    /// </summary>
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

    /// <summary>
    /// 从文件末尾向前读取，跳过表头、空行和未写完整的末行，
    /// 收集最多 <paramref name="maxCount"/> 条记录，并按时间正序返回。
    /// </summary>
    private static List<CrimpData> ParseFileFromEnd(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken,
        out long bytesRead)
    {
        bytesRead = 0;
        // 倒序收集：集满 maxCount 即停止；末尾为最新，稍后整体反转成正序。
        var reversed = new List<CrimpData>(maxCount);

        try
        {
            // 使用 FileStream 并指定 FileShare.ReadWrite 允许其他进程同时写入
            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite  // 关键：允许其他进程写入
            );

            // 倒读上限 = 所需条数 + 缓冲。缓冲用于吸收：可能被丢弃的半截末行、
            // 末尾偶发的无效行（高度=0 的预热段等）。只读末尾这么多行即停，
            // 不再读到文件头，使单次开销与文件总大小（可达数十万行）解耦。
            const int tailBuffer = 16;
            var lines = ReadLinesFromEnd(fileStream, maxCount + tailBuffer, cancellationToken, out bytesRead);

            // 末行若未以换行结束，视为正在写入的半条记录，丢弃不解析。
            if (lines.Count > 0 && !EndsWithLineTerminator(fileStream))
                lines.RemoveAt(0);

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reversed.Count >= maxCount) break;

                // 跳过空行。
                if (string.IsNullOrWhiteSpace(line)) continue;

                var data = ParseLine(line, filePath);
                if (data != null)
                    reversed.Add(data);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            // 文件暂时被锁定，跳过本次读取
            System.Diagnostics.Debug.WriteLine($"[CSV] 文件被跳过（可能正在写入）: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 读取失败 {Path.GetFileName(filePath)}: {ex.Message}");
        }

        // 倒序收集的结果反转为时间正序，保证趋势和漂移计算顺序正确。
        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// 从文件末尾向前逐行读取，返回从新到旧排列的行（第 0 项为物理末行）。
    /// 表头（文件首行）不会包含在返回结果中。
    /// 收集到 <paramref name="maxLines"/> 行后立即停止，不再向文件头继续读取，
    /// 使单次读取开销与文件总大小无关（文件可增长到数十万行）。
    /// </summary>
    private static List<string> ReadLinesFromEnd(
        FileStream fileStream,
        int maxLines,
        CancellationToken cancellationToken,
        out long bytesRead)
    {
        bytesRead = 0;
        var lines = new List<string>();
        if (maxLines <= 0) return lines;

        long length = fileStream.Length;
        if (length == 0) return lines;

        const int bufferSize = 8192;
        var buffer = new byte[bufferSize];
        long position = length;
        // 跨块缓存当前（尚未遇到换行符的）行字节。倒读时字节是逆序出现的，
        // 故放入时追加（逆序），凑成整行后一次性反转再解码，避免逐字节头插的 O(n²) 开销。
        var pending = new List<byte>();

        while (position > 0 && lines.Count < maxLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int readSize = (int)Math.Min(bufferSize, position);
            position -= readSize;
            fileStream.Seek(position, SeekOrigin.Begin);
            ReadExactly(fileStream, buffer, readSize);
            bytesRead += readSize;

            for (int i = readSize - 1; i >= 0; i--)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    lines.Add(DecodeReversed(pending));
                    pending.Clear();
                    if (lines.Count >= maxLines)
                        break; // 已集满所需行数，停止扫描本块并退出外层循环。
                }
                else if (b != (byte)'\r')
                {
                    pending.Add(b); // 逆序追加（O(1)），整行完成后统一反转。
                }
            }
        }

        // 文件首行即表头：仅当确实读到了文件开头（position == 0）时，
        // 剩余 pending 才是表头，直接丢弃。若因集满 maxLines 提前停止，
        // pending 是一条更旧记录的残片，同样丢弃（不足一整行，不解析）。
        return lines;
    }

    /// <summary>
    /// 将“逆序存放”的行字节反转为正序后用 UTF-8 解码。
    /// </summary>
    private static string DecodeReversed(List<byte> reversedBytes)
    {
        if (reversedBytes.Count == 0) return string.Empty;
        var bytes = new byte[reversedBytes.Count];
        int n = reversedBytes.Count;
        for (int i = 0; i < n; i++)
            bytes[i] = reversedBytes[n - 1 - i];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static void ReadExactly(FileStream fileStream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = fileStream.Read(buffer, offset, count - offset);
            if (read == 0)
                throw new EndOfStreamException("读取 CSV 时意外到达文件结尾。");
            offset += read;
        }
    }

    private static CrimpData? ParseLine(string line, string filePath)
    {
        var parts = line.Split(',');
        if (parts.Length < 7) return null;

        return new CrimpData
        {
            Timestamp = parts[0].Trim(),
            RecordedAt = ParseTimestamp(parts[0], filePath) ?? File.GetLastWriteTime(filePath),
            Head1Height = ParseDouble(parts[1]),
            Head2Height = ParseDouble(parts[3])
        };
    }

    private static bool EndsWithLineTerminator(FileStream fileStream)
    {
        if (fileStream.Length == 0) return false;

        fileStream.Seek(-1, SeekOrigin.End);
        int lastByte = fileStream.ReadByte();
        return lastByte is '\r' or '\n';
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
        return 0; // 无效数据以零表示；CrimpData 会将零高度排除。
    }

    private static DateTime? ParseTimestamp(string s, string filePath)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        string value = s.Trim();
        string[] dateTimeFormats = {
            "yyyyMMddHHmmssfff",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "yyyyMMdd HHmmss",
        };

        if (DateTime.TryParseExact(
            value,
            dateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dt))
        {
            return dt;
        }

        string[] timeFormats = { "HH-mm-ss-fff", "HH-mm-ss" };
        if (DateTime.TryParseExact(
                value,
                timeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time)
            && TryGetFileDate(filePath, out var fileDate))
        {
            return fileDate.Add(time.TimeOfDay);
        }

        return null;
    }

    private static bool TryGetFileDate(string filePath, out DateTime date)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        int separator = fileName.LastIndexOf('_');
        string datePart = separator >= 0 ? fileName[(separator + 1)..] : fileName;
        return DateTime.TryParseExact(
            datePart,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static DateTime GetLastWriteTimeUtcSafe(string filePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(filePath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime GetFileDateSafe(string filePath) =>
        TryGetFileDate(filePath, out var date) ? date : DateTime.MinValue;
}
