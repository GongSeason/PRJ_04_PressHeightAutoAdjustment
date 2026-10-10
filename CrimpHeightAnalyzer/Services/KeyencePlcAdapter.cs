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
            string address = headIndex == 0 ? _settings.HeadAAdjustAddress : _settings.HeadBAdjustAddress;
            string headName = headIndex == 0 ? "压头A" : "压头B";

            // 将 mm 转换为 PLC 单位（保留 5 位小数精度，乘以 100000）
            int plcValue = checked((int)Math.Round(
                deltaMm * 100000,
                MidpointRounding.AwayFromZero));

            var result = await _plc.WriteAsync(address, plcValue);

            if (result.IsSuccess)
            {
                OnLog?.Invoke($"[PLC] {headName} 调整 {deltaMm:F5}mm 写入 {address}");
                return true;
            }

            OnLog?.Invoke($"[PLC ERROR] {headName} 写入失败: {result.Message}");
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
    /// 发送停机指令
    /// </summary>
    public async Task<bool> SendStopAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;

        if (!await EnsureConnectedAsync(cancellationToken))
        {
            OnLog?.Invoke($"[PLC] 跳过停机: 未连接");
            return false;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // 写入停机信号（1 = 停机）
            var result = await _plc.WriteAsync(_settings.StopSignalAddress, 1);

            if (result.IsSuccess)
            {
                OnLog?.Invoke($"[PLC] 停机信号已发送: {reason}");
                return true;
            }

            OnLog?.Invoke($"[PLC ERROR] 停机信号发送失败: {result.Message}");
            IsConnected = false;
            ScheduleReconnect();
            OnConnectionChanged?.Invoke(false, $"停机失败: {result.Message}");
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 读取压头当前位置
    /// </summary>
    public async Task<double?> ReadHeadPositionAsync(int headIndex, CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        if (!await EnsureConnectedAsync(cancellationToken))
        {
            return null;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            string address = headIndex == 0 ? _settings.HeadAPositionAddress : _settings.HeadBPositionAddress;
            string headName = headIndex == 0 ? "压头A" : "压头B";

            // 读取 32 位浮点数
            var result = await _plc.ReadFloatAsync(address);

            if (result.IsSuccess)
            {
                OnLog?.Invoke($"[PLC] {headName} 位置: {result.Content:F5}mm (从 {address})");
                return result.Content;
            }
            else
            {
                OnLog?.Invoke($"[PLC ERROR] {headName} 位置读取失败: {result.Message}");
                IsConnected = false;
                ScheduleReconnect();
                OnConnectionChanged?.Invoke(false, $"读取失败: {result.Message}");
                return null;
            }
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
