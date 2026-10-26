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
    /// 批量发送两个压头的调整指令（一次通信写入连续寄存器）。
    /// 要求两个压头的寄存器地址连续（如 D2450~D2453）。
    /// </summary>
    /// <param name="headADeltaMm">压头A 调整量 (mm)</param>
    /// <param name="headBDeltaMm">压头B 调整量 (mm)</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>(压头A是否成功, 压头B是否成功)</returns>
    Task<(bool HeadAOk, bool HeadBOk)> SendBatchAdjustmentAsync(double headADeltaMm, double headBDeltaMm, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取 PLC 指定地址的 16 位整数值
    /// </summary>
    /// <param name="address">寄存器地址（如 D2440）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>读取结果；失败时返回 null</returns>
    Task<short?> ReadInt16Async(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向 PLC 指定地址写入 16 位整数值
    /// </summary>
    /// <param name="address">寄存器地址（如 D2459）</param>
    /// <param name="value">要写入的值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否写入成功</returns>
    Task<bool> WriteInt16Async(string address, short value, CancellationToken cancellationToken = default);
}
