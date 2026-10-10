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
        return Task.FromResult(true);
    }

    public Task<bool> SendStopAsync(string reason, CancellationToken cancellationToken = default)
    {
        OnLog?.Invoke($"[PLC Mock] 停机: {reason}");
        return Task.FromResult(true);
    }

    public Task<double?> ReadHeadPositionAsync(int headIndex, CancellationToken cancellationToken = default)
    {
        string head = headIndex == 0 ? "压头A" : "压头B";
        // Mock 返回随机位置用于测试
        double position = 12.60 + new Random().NextDouble() * 0.1 - 0.05;
        OnLog?.Invoke($"[PLC Mock] {head} 位置: {position:F5}mm");
        return Task.FromResult<double?>(position);
    }
}
