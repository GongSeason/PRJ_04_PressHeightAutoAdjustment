namespace CrimpHeight.Core.Models;

/// <summary>
/// 压头状态 — PRD §C 四状态机
/// </summary>
public enum HeadState
{
    /// <summary>置信不足 — 样本不够 / 刚换模 / epoch 刚重置</summary>
    WarmUp,

    /// <summary>稳定 — 偏差在死区内，DriftScore 低</summary>
    Stable,

    /// <summary>漂移 — 偏差越界或 DriftScore 确认</summary>
    Drift,

    /// <summary>异常 — 安全护栏触发</summary>
    Outlier
}

/// <summary>
/// 发散等级 — PRD §D 三级
/// </summary>
public enum DivergenceLevel
{
    /// <summary>|D| < T — 正常</summary>
    Normal,

    /// <summary>T ≤ |D| ≤ 2T — 降级（K 折半）</summary>
    Degraded,

    /// <summary>|D| > 2T — 停机</summary>
    Stop
}

/// <summary>
/// 调整方向
/// </summary>
public enum AdjustmentDirection
{
    /// <summary>不调整</summary>
    None,

    /// <summary>压头上升</summary>
    Up,

    /// <summary>压头下降</summary>
    Down
}
