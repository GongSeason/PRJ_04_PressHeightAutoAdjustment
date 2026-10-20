using CrimpHeight.Core.Interfaces;
using CrimpHeightAnalyzer.Models;
using HslCommunication;
using HslCommunication.Profinet.Keyence;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// Keyence PLC 适配器 — 使用 HslCommunication 库实现 MC Qna 3E Binary 协议通信
/// </summary>
public class KeyencePlcAdapter : IPlcAdapter, IDisposable
{
    private readonly KeyenceMcNet _plc;
    private readonly PlcSettings _settings;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _reconnectSync = new();
    private DateTime _nextReconnectAttemptUtc = DateTime.MinValue;
    private bool _disposed;

    public event Action<string>? OnLog;
    public event Action<bool, string>? OnConnectionChanged;

    public bool IsConnected { get; private set; }

    public KeyencePlcAdapter(PlcSettings settings)
    {
        _settings = settings;
        _plc = new KeyenceMcNet
        {
            IpAddress = settings.IpAddress,
            Port = settings.Port,
            ConnectTimeOut = settings.ConnectTimeoutMs
        };
        _plc.SetPersistentConnection();
    }

    /// <summary>
    /// 建立 PLC 连接
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        await _semaphore.WaitAsync(ct);
        try
        {
            OperateResult result;
            try
            {
                result = await Task.Run(() => _plc.ConnectServer(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IsConnected = false;
                ScheduleReconnect();
                OnLog?.Invoke($"[PLC] 连接异常: {ex.Message}");
                OnConnectionChanged?.Invoke(false, $"连接异常: {ex.Message}");
                return false;
            }

            if (_disposed)
            {
                if (result.IsSuccess)
                    _plc.ConnectClose();
                return false;
            }

            IsConnected = result.IsSuccess;

            if (result.IsSuccess)
            {
                ResetReconnectSchedule();
                OnLog?.Invoke($"[PLC] 已连接到 {_settings.IpAddress}:{_settings.Port}");
                OnConnectionChanged?.Invoke(true, "已连接");
            }
            else
            {
                ScheduleReconnect();
                OnLog?.Invoke($"[PLC] 连接失败: {result.Message}");
                OnConnectionChanged?.Invoke(false, $"连接失败: {result.Message}");
            }

            return result.IsSuccess;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 确保连接可用，失败时尝试重连
    /// </summary>
    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return true;

        lock (_reconnectSync)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextReconnectAttemptUtc)
                return false;

            _nextReconnectAttemptUtc = now.AddMilliseconds(_settings.ReconnectIntervalMs);
        }

        OnLog?.Invoke("[PLC] 尝试重新连接...");
        return await ConnectAsync(ct);
    }

    /// <summary>
    /// 发送压头调整指令
    /// </summary>
    public async Task<bool> SendAdjustmentAsync(int headIndex, double deltaMm, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;

        if (!await EnsureConnectedAsync(cancellationToken))
        {
            OnLog?.Invoke($"[PLC] 跳过调整: 未连接");
            return false;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            string address = (headIndex == 0 ? _settings.HeadAAdjustAddress : _settings.HeadBAdjustAddress)?.Trim() ?? "";
            string statusAddress = headIndex == 0 ? _settings.HeadAStatusAddress : _settings.HeadBStatusAddress;
            string headName = headIndex == 0 ? "压头A" : "压头B";

            // P1-2: 写入前检查状态标志，若仍为 1 则上一条指令未被 PLC 处理
            var statusReadResult = await _plc.ReadInt16Async(statusAddress);
            if (statusReadResult.IsSuccess && statusReadResult.Content != 0)
            {
                OnLog?.Invoke($"[PLC] {headName} 跳过：上一条指令未被处理 (标志={statusReadResult.Content})");
                return false;
            }

            // 需求4: 写入 32 位整数（mm × 1000 取整），PLC 侧已改为接收整数
            int plcValue = (int)Math.Round(deltaMm * 1000);

            OnLog?.Invoke($"[PLC] {headName} 准备写入: 地址=\"{address}\", 值={plcValue} (原始={deltaMm:F5}mm × 1000)");
            var result = await _plc.WriteAsync(address, plcValue);

            if (!result.IsSuccess)
            {
                OnLog?.Invoke($"[PLC ERROR] {headName} 偏移值写入失败: {result.Message}");
                IsConnected = false;
                ScheduleReconnect();
                OnConnectionChanged?.Invoke(false, $"写入失败: {result.Message}");
                return false;
            }

            // P1-2: 回读校验（int32）
            var readBackResult = await _plc.ReadInt32Async(address);
            if (readBackResult.IsSuccess)
            {
                int readBack = readBackResult.Content;
                if (readBack != plcValue)
                {
                    OnLog?.Invoke($"[PLC ERROR] {headName} 回读校验失败: 写入={plcValue}, 读回={readBack}");
                    return false;
                }
                OnLog?.Invoke($"[PLC] {headName} 回读校验通过: {readBack}");
            }
            else
            {
                OnLog?.Invoke($"[PLC WARN] {headName} 回读失败（写入可能成功）: {readBackResult.Message}");
                // 回读失败不阻塞，继续置标志
            }

            OnLog?.Invoke($"[PLC] {headName} 调整 {deltaMm:F5}mm (PLC值={plcValue}) 写入 {address}");

            // 写入完成状态标志 = 1，通知 PLC 新偏移值已就绪
            var statusResult = await _plc.WriteAsync(statusAddress, (short)1);

            if (statusResult.IsSuccess)
            {
                OnLog?.Invoke($"[PLC] {headName} 状态标志已置1 ({statusAddress})");
                return true;
            }

            // P1-2: 修复 bug，原来引用的是 result.Message，应为 statusResult.Message
            OnLog?.Invoke($"[PLC ERROR] {headName} 状态标志写入失败: {statusResult.Message}");
            IsConnected = false;
            ScheduleReconnect();
            OnConnectionChanged?.Invoke(false, $"状态标志写入失败: {statusResult.Message}");
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 断开连接并释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _plc?.ConnectClose();
            OnLog?.Invoke("[PLC] 连接已关闭");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[PLC] 关闭连接时出错: {ex.Message}");
        }

        GC.SuppressFinalize(this);
    }

    private void ScheduleReconnect()
    {
        lock (_reconnectSync)
        {
            _nextReconnectAttemptUtc = DateTime.UtcNow.AddMilliseconds(_settings.ReconnectIntervalMs);
        }
    }

    private void ResetReconnectSchedule()
    {
        lock (_reconnectSync)
        {
            _nextReconnectAttemptUtc = DateTime.MinValue;
        }
    }
}
