using System.IO;
using System.Text.Json;
using CrimpHeightAnalyzer.Models;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// 停机水位线持久化服务。
///
/// 把"已确认停机"的进度落盘到 stop_state.json（与 settings.json 同目录），
/// 使应用/设备重启后能识别"又读到停机当时或更早的历史不良数据"，
/// 从而不重复下发停机；仅当出现晚于水位线的新生产数据时才恢复正常判定。
/// </summary>
public class StopStateService
{
    private static readonly string StatePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "stop_state.json");

    /// <summary>
    /// 加载停机水位线状态。文件不存在或损坏时返回空状态（无水位线）。
    /// </summary>
    public StopState Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var state = JsonSerializer.Deserialize<StopState>(json) ?? new StopState();
                state.HeadA ??= new();
                state.HeadB ??= new();
                return state;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载停机状态失败: {ex.Message}");
        }

        return new StopState();
    }

    /// <summary>
    /// 保存停机水位线状态。
    /// </summary>
    public bool Save(StopState state)
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
            System.Diagnostics.Debug.WriteLine($"保存停机状态失败: {ex.Message}");
            return false;
        }
    }
}
