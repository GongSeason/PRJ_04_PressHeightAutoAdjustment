using System.Text.Json.Serialization;
using CrimpHeight.Core.Models;

namespace CrimpHeightAnalyzer.Models;

/// <summary>
/// 应用配置 — UI 参数 + 控制参数
/// </summary>
public class AppSettings
{
    // === UI 参数 ===

    /// <summary>监视文件夹路径</summary>
    public string MonitorPath { get; set; } = @"E:\TE_connectivity\5_VibeCoding\PressHeightAutoAdjustment\saved data\CCD6";

    /// <summary>扫描天数范围（0=全部）</summary>
    public int ScanDays { get; set; } = 0;

    /// <summary>自动刷新间隔（秒）</summary>
    public int RefreshIntervalSeconds { get; set; } = 10;

    // === 控制参数 ===

    /// <summary>控制器配置</summary>
    public ControllerConfig Controller { get; set; } = new();

    // === PLC 通信参数 ===

    /// <summary>PLC 通信配置</summary>
    public PlcSettings Plc { get; set; } = new();

    // === 便捷属性 ===

    [JsonIgnore]
    public double TargetMidpoint => Controller.TargetMidpoint;
}

/// <summary>
/// PLC 通信配置
/// </summary>
public class PlcSettings
{
    /// <summary>PLC IP 地址</summary>
    public string IpAddress { get; set; } = "192.168.1.10";

    /// <summary>PLC 端口号</summary>
    public int Port { get; set; } = 5000;

    /// <summary>连接超时时间（毫秒）</summary>
    public int ConnectTimeoutMs { get; set; } = 5000;

    /// <summary>是否使用真实 PLC（false=使用 Mock 适配器）</summary>
    public bool UseRealPlc { get; set; } = false;

    /// <summary>压头 A 调整量寄存器地址</summary>
    public string HeadAAdjustAddress { get; set; } = "D100";

    /// <summary>压头 B 调整量寄存器地址</summary>
    public string HeadBAdjustAddress { get; set; } = "D102";

    /// <summary>压头 A 写入完成状态地址（PC 写1=已就绪，PLC 写0=已取走）</summary>
    public string HeadAStatusAddress { get; set; } = "D200";

    /// <summary>压头 B 写入完成状态地址（PC 写1=已就绪，PLC 写0=已取走）</summary>
    public string HeadBStatusAddress { get; set; } = "D202";

    /// <summary>重连间隔时间（毫秒）</summary>
    public int ReconnectIntervalMs { get; set; } = 10000;

    /// <summary>
    /// 跳过握手检查。默认 false（走正常握手：写入前检查标志是否为 0）。
    /// 设为 true 时忽略握手，直接写偏移值 + 置标志=1，用于 PLC 侧握手未就绪时的测试。
    /// </summary>
    public bool BypassHandshake { get; set; } = false;
}
