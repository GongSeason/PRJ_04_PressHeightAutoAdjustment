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
            string headName = headIndex == 0 ? "压头A" : "压头B";

            // 直接以浮点数写入 PLC 寄存器（32 位 float）
            float plcValue = (float)deltaMm;

            OnLog?.Invoke($"[PLC] {headName} 准备写入: 地址=\"{address}\" (长度={address.Length}, bytes={BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(address))}), 值={plcValue}");
            var result = await _plc.WriteAsync(address, plcValue);

            if (!result.IsSuccess)
            {
                OnLog?.Invoke($"[PLC ERROR] {headName} 偏移值写入失败: {result.Message}");
                IsConnected = false;
                ScheduleReconnect();
                OnConnectionChanged?.Invoke(false, $"写入失败: {result.Message}");
                return false;
            }

            OnLog?.Invoke($"[PLC] {headName} 调整 {deltaMm:F5}mm 写入 {address}");

            // 写入完成状态标志 = 1，通知 PLC 新偏移值已就绪
            string statusAddress = headIndex == 0 ? _settings.HeadAStatusAddress : _settings.HeadBStatusAddress;
            var statusResult = await _plc.WriteAsync(statusAddress, (short)1);

            if (statusResult.IsSuccess)
            {
                OnLog?.Invoke($"[PLC] {headName} 状态标志已置1 ({statusAddress})");
                return true;
            }

            OnLog?.Invoke($"[PLC ERROR] {headName} 状态标志写入失败: {statusResult.Message}");
            IsConnected = false;
            ScheduleReconnect();
            OnConnectionChanged?.Invoke(false, $"写入失败: {result.Message}");
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
