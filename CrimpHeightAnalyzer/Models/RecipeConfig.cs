namespace CrimpHeightAnalyzer.Models;

/// <summary>
/// 配方管理配置（保存到本地 JSON）
/// </summary>
public class RecipeConfig
{
    /// <summary>PLC 程序号读取地址（如 D2440）</summary>
    public string RecipePollingAddress { get; set; } = "D2440";

    /// <summary>程序号轮询间隔（秒）</summary>
    public int RecipePollingIntervalSeconds { get; set; } = 3;

    /// <summary>心跳信号写入地址（如 D2459）</summary>
    public string HeartbeatAddress { get; set; } = "D2459";

    /// <summary>心跳信号间隔（毫秒）</summary>
    public int HeartbeatIntervalMs { get; set; } = 2000;

    /// <summary>配方列表</summary>
    public List<Recipe> Recipes { get; set; } = new();
}

/// <summary>
/// 单个配方定义
/// </summary>
public class Recipe
{
    /// <summary>PLC 程序号（对应 PLC 地址中读到的值）</summary>
    public int ProgramId { get; set; }

    /// <summary>配方别名（界面显示用，如料号）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>该配方对应的 CSV 监视文件夹路径</summary>
    public string MonitorPath { get; set; } = string.Empty;

    /// <summary>铆压#1 手动偏移量 (mm)</summary>
    public double HeadAManualDelta { get; set; }

    /// <summary>铆压#2 手动偏移量 (mm)</summary>
    public double HeadBManualDelta { get; set; }
}
