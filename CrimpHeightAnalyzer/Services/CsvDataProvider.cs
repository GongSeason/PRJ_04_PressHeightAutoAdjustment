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
            var latestFile = GetLatestCsvFile();
            var allData = latestFile != null
                ? ParseFileFromEnd(latestFile, count, cancellationToken)
                : new List<CrimpData>();

            LastDataTimestamp = allData.Count > 0 ? allData[^1].RecordedAt : null;

            return allData;
        }, cancellationToken);
    }

    private string? GetLatestCsvFile()
    {
        if (!Directory.Exists(_basePath))
            return null;

        var cutoff = _scanDays > 0 ? DateTime.Today.AddDays(-_scanDays) : DateTime.MinValue;
        // 递归搜索所有子目录：实际 CSV 位于 <监视根目录>\<日期文件夹>\Data_*_<日期>.csv，
        // 而不在监视根目录本层。日期文件夹之上还可能存在额外父级（如 F2 的上级），
        // AllDirectories 可一并覆盖，无需关心具体层数。
        return EnumerateCsvFilesSafe(_basePath)
            .Where(file => TryGetFileDate(file, out var date) && date >= cutoff)
            .OrderByDescending(GetFileDateSafe)
            .ThenByDescending(GetLastWriteTimeUtcSafe)
            .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// 递归枚举监视根目录下的所有 CSV 文件。
    /// 使用逐项枚举以便忽略个别无法访问的子目录（权限/被占用），不让整次扫描失败。
    /// </summary>
    private static IEnumerable<string> EnumerateCsvFilesSafe(string basePath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // 不要跳过 ReparsePoint：OneDrive / 云盘“按需同步”会把日期子文件夹标记为
            // ReparsePoint，若跳过会导致递归无法进入这些目录、扫不到任何 CSV
            // （现象：MonitorPath 指到 F2 根目录时没有数据，直接指到当天日期文件夹才有）。
            // 仅跳过隐藏/系统目录；数据目录为普通目录树，不存在符号链接环的风险。
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MaxRecursionDepth = 8
        };

        try
        {
            return Directory.EnumerateFiles(basePath, "*.csv", options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CSV] 枚举文件失败 {basePath}: {ex.Message}");
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// 从文件末尾向前读取，跳过表头、空行和未写完整的末行，
    /// 收集最多 <paramref name="maxCount"/> 条记录，并按时间正序返回。
    /// </summary>
    private static List<CrimpData> ParseFileFromEnd(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken)
    {
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

            var lines = ReadLinesFromEnd(fileStream, cancellationToken);

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
    /// </summary>
    private static List<string> ReadLinesFromEnd(
        FileStream fileStream,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        long length = fileStream.Length;
        if (length == 0) return lines;

        const int bufferSize = 8192;
        var buffer = new byte[bufferSize];
        long position = length;
        var pending = new List<byte>(); // 跨块缓存当前（尚未遇到换行符的）行字节，正序存放。

        while (position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int readSize = (int)Math.Min(bufferSize, position);
            position -= readSize;
            fileStream.Seek(position, SeekOrigin.Begin);
            ReadExactly(fileStream, buffer, readSize);

            for (int i = readSize - 1; i >= 0; i--)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    lines.Add(DecodeLine(pending));
                    pending.Clear();
                }
                else if (b != (byte)'\r')
                {
                    pending.Insert(0, b);
                }
            }
        }

        // 文件首行即表头：剩余 pending 不加入结果，直接丢弃以跳过表头。
        return lines;
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

    private static string DecodeLine(List<byte> bytes) =>
        bytes.Count == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes.ToArray());

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
