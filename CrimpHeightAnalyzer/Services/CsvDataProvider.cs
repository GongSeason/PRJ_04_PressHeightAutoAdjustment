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
            var csvFiles = GetCsvFiles();
            var allData = new List<CrimpData>(count);

            foreach (var file in csvFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int remaining = count - allData.Count;
                if (remaining <= 0) break;

                // 文件按新到旧处理；旧文件数据要插在已读取的新数据之前。
                var fileData = ParseFile(file, remaining, cancellationToken);
                if (fileData.Count > 0)
                    allData.InsertRange(0, fileData);
            }

            LastDataTimestamp = allData.Count > 0 ? allData[^1].RecordedAt : null;

            return allData;
        }, cancellationToken);
    }

    private List<string> GetCsvFiles()
    {
        if (!Directory.Exists(_basePath))
            return new List<string>();

        var cutoff = _scanDays > 0 ? DateTime.Today.AddDays(-_scanDays) : DateTime.MinValue;
        return Directory.GetFiles(_basePath, "*.csv", SearchOption.TopDirectoryOnly)
            .Where(file => TryGetFileDate(file, out var date) && date >= cutoff)
            .OrderByDescending(GetFileDateSafe)
            .ThenByDescending(GetLastWriteTimeUtcSafe)
            .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CrimpData> ParseFile(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var result = new Queue<CrimpData>(maxCount);

        try
        {
            // 使用 FileStream 并指定 FileShare.ReadWrite 允许其他进程同时写入
            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite  // 关键：允许其他进程写入
            );

            using var reader = new StreamReader(fileStream);
            _ = reader.ReadLine(); // 跳过表头

            // 延迟一行解析：文件正在写入且末行未换行时，不消费半条记录。
            string? pendingLine = null;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingLine != null)
                    AddParsedLine(pendingLine, filePath, result, maxCount);
                pendingLine = line;
            }

            if (pendingLine != null && EndsWithLineTerminator(fileStream))
                AddParsedLine(pendingLine, filePath, result, maxCount);
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

        return result.ToList();
    }

    private static void AddParsedLine(
        string line,
        string filePath,
        Queue<CrimpData> result,
        int maxCount)
    {
        var parts = line.Split(',');
        if (parts.Length < 7) return;

        var data = new CrimpData
        {
            Timestamp = parts[0].Trim(),
            RecordedAt = ParseTimestamp(parts[0], filePath) ?? File.GetLastWriteTime(filePath),
            Head1Height = ParseDouble(parts[1]),
            Head2Height = ParseDouble(parts[3])
        };

        if (result.Count == maxCount)
            result.Dequeue();
        result.Enqueue(data);
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
