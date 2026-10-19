using System.IO;
using System.Text.Json;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// 累计补偿值持久化服务。
/// 将两个压头的累计补偿值落盘到 compensation_state.json，
/// 应用重启后恢复，避免重启后累计值归零而 PLC 寄存器仍保留旧补偿。
/// </summary>
public class CompensationStateService
{
    private static readonly string StatePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "compensation_state.json");

    /// <summary>
    /// 加载累计补偿状态。文件不存在或损坏时返回零值。
    /// </summary>
    public CompensationState Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                return JsonSerializer.Deserialize<CompensationState>(json) ?? new CompensationState();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载补偿状态失败: {ex.Message}");
        }

        return new CompensationState();
    }

    /// <summary>
    /// 保存累计补偿状态。
    /// </summary>
    public bool Save(CompensationState state)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(state, options);
            File.WriteAllText(StatePath, json);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存补偿状态失败: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// 累计补偿持久化数据
/// </summary>
public class CompensationState
{
    /// <summary>压头 A 累计补偿 (mm)</summary>
    public double HeadAAccumulated { get; set; }

    /// <summary>压头 B 累计补偿 (mm)</summary>
    public double HeadBAccumulated { get; set; }

    /// <summary>最后保存时间</summary>
    public DateTime? LastSavedAt { get; set; }
}
