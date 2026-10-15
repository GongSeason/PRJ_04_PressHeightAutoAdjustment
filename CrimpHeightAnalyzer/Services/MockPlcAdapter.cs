using CrimpHeight.Core.Interfaces;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// PLC Mock 适配器 — v1 仅日志输出
/// </summary>
public class MockPlcAdapter : IPlcAdapter
{
    public event Action<string>? OnLog;

    public Task<bool> SendAdjustmentAsync(int headIndex, double deltaMm, CancellationToken cancellationToken = default)
    {
        string head = headIndex == 0 ? "压头A" : "压头B";
        OnLog?.Invoke($"[PLC Mock] {head} 调整 {deltaMm:F5}mm");
        OnLog?.Invoke($"[PLC Mock] {head} 状态标志已置1");
        return Task.FromResult(true);
    }


}
