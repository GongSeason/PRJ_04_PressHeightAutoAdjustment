namespace CrimpHeight.Core.Models;

/// <summary>
/// 单条压接数据记录：每拍分别记录两个压头的单个产品高度。
/// </summary>
public class CrimpData
{
    public string Timestamp { get; set; } = string.Empty;
    public DateTime? RecordedAt { get; set; }
    public double Head1Height { get; set; }
    public double Head2Height { get; set; }

    public bool IsHead1Valid => IsValidMeasurement(Head1Height);
    public bool IsHead2Valid => IsValidMeasurement(Head2Height);
    public bool IsValid => IsHead1Valid || IsHead2Valid;

    private static bool IsValidMeasurement(double value) =>
        double.IsFinite(value) && value > 0;
}
