using CrimpHeight.Core.Interfaces;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// PLC Mock 适配器 — 模拟标志握手行为，便于回放测试。
/// 写 1 后下次调用前自动清 0（模拟 PLC 取走偏移值后清零标志）。
/// </summary>
public class MockPlcAdapter : IPlcAdapter
{
    public event Action<string>? OnLog;

    // P1-2: 模拟状态标志（写 1 后下次调用前自动清 0）
    private readonly int[] _statusFlags = new int[2]; // 0=A, 1=B

    // Mock 寄存器存储，用于模拟读写
    private readonly Dictionary<string, short> _registers = new();

    public Task<bool> SendAdjustmentAsync(int headIndex, double deltaMm, CancellationToken cancellationToken = default)
    {
        string head = headIndex == 0 ? "压头A" : "压头B";
        int idx = headIndex is 0 or 1 ? headIndex : 0;

        // 模拟握手：检查标志是否已清零
        if (_statusFlags[idx] != 0)
        {
            OnLog?.Invoke($"[PLC Mock] {head} 跳过：上一条指令未被处理 (标志={_statusFlags[idx]})");
            // 模拟 PLC 处理完成：清零标志（下次调用即可写入）
            _statusFlags[idx] = 0;
            return Task.FromResult(false);
        }

        int plcIntValue = (int)Math.Round(deltaMm * 1000);
        OnLog?.Invoke($"[PLC Mock] {head} 调整 {deltaMm:F5}mm (PLC值={plcIntValue})");
        // 置标志=1
        _statusFlags[idx] = 1;
        OnLog?.Invoke($"[PLC Mock] {head} 状态标志已置1");

        // 模拟 PLC 立即处理完成：清零标志
        _statusFlags[idx] = 0;

        return Task.FromResult(true);
    }

    public Task<short?> ReadInt16Async(string address, CancellationToken cancellationToken = default)
    {
        short value = _registers.TryGetValue(address, out var v) ? v : (short)0;
        OnLog?.Invoke($"[PLC Mock] 读取 {address} = {value}");
        return Task.FromResult<short?>(value);
    }

    public Task<bool> WriteInt16Async(string address, short value, CancellationToken cancellationToken = default)
    {
        _registers[address] = value;
        OnLog?.Invoke($"[PLC Mock] 写入 {address} = {value}");
        return Task.FromResult(true);
    }
}
