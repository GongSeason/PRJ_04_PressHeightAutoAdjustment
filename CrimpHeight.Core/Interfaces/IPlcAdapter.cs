namespace CrimpHeight.Core.Interfaces;

/// <summary>
/// PLC 通信抽象 — 解耦具体协议（v1 用 Mock）
/// </summary>
public interface IPlcAdapter
{
    /// <summary>
    /// 发送压头调整指令
    /// </summary>
    /// <param name="headIndex">压头索引（0=A, 1=B）</param>
    /// <param name="deltaMm">调整量 (mm)，正=下降，负=上升</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>指令是否成功写入 PLC</returns>
    Task<bool> SendAdjustmentAsync(int headIndex, double deltaMm, CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送停机指令
    /// </summary>
    /// <param name="reason">停机原因</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>指令是否成功写入 PLC</returns>
    Task<bool> SendStopAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取压头当前位置
    /// </summary>
    /// <param name="headIndex">压头索引（0=A, 1=B）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>当前位置值（mm），读取失败返回 null</returns>
    Task<double?> ReadHeadPositionAsync(int headIndex, CancellationToken cancellationToken = default);
}
