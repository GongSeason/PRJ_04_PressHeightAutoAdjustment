namespace CrimpHeight.Core.Models;

/// <summary>
/// 控制器配置 — 精简为 v1 必要参数
/// </summary>
public class ControllerConfig
{
    // === 规格参数 ===

    /// <summary>压接高度上限 (mm)</summary>
    public double UpperLimit { get; set; } = 12.65;

    /// <summary>压接高度下限 (mm)</summary>
    public double LowerLimit { get; set; } = 12.55;

    /// <summary>容差上限 (mm) — 偏差向上容忍量，超过此值触发调整</summary>
    public double UpperTolerance { get; set; } = 0.02;

    /// <summary>容差下限 (mm) — 偏差向下容忍量，超过此值触发调整</summary>
    public double LowerTolerance { get; set; } = 0.02;

    // === 控制律参数 ===

    /// <summary>控制器增益 K（K-only 阻尼模式）</summary>
    public double ControllerGain { get; set; } = 0.5;

    /// <summary>单次调整最大步长 (mm)</summary>
    public double MaxStepPerAdjust { get; set; } = 0.01;

    // === 统计参数 ===

    /// <summary>截尾比例 (0.05 = 5%)</summary>
    public double TrimPercentage { get; set; } = 0.05;

    /// <summary>分析窗口大小 n — 取最近 n 条有效数据参与计算</summary>
    public int WindowSize { get; set; } = 200;

    /// <summary>每 epoch 最少样本数 — 低于此判 WarmUp，不动作</summary>
    public int MinSamplesPerEpoch { get; set; } = 50;

    // === 发散参数 ===

    /// <summary>发散阈值 T (mm) — |D| 超过此值开始分级</summary>
    public double DivergenceThreshold { get; set; } = 0.02;

    /// <summary>degraded 区 K 折减系数（0.5 = K 减半）</summary>
    public double DegradedGainFactor { get; set; } = 0.5;

    /// <summary>Drift 确认连续同向拍数 N</summary>
    public int DriftConfirmCycles { get; set; } = 3;

    /// <summary>Drift slope 最小幅度门限 (mm/拍) — |slope| 低于此值不判漂移，过滤噪声</summary>
    public double DriftSlopeThreshold { get; set; } = 0.0001;

    // === 目标点 ===

    /// <summary>
    /// 目标点 (mm)。可独立配置，不必等于规格中点。
    /// 若未配置（保持默认 0），Validate() 会自动设为 (UpperLimit + LowerLimit) / 2。
    /// </summary>
    public double TargetMidpoint { get; set; }

    public void Validate()
    {
        RequireFinite(UpperLimit, nameof(UpperLimit));
        RequireFinite(LowerLimit, nameof(LowerLimit));
        if (UpperLimit <= LowerLimit)
            throw new InvalidOperationException("压接高度上限必须大于下限。");

        RequireNonNegative(UpperTolerance, nameof(UpperTolerance));
        RequireNonNegative(LowerTolerance, nameof(LowerTolerance));
        RequireNonNegative(ControllerGain, nameof(ControllerGain));
        RequirePositive(MaxStepPerAdjust, nameof(MaxStepPerAdjust));

        if (!double.IsFinite(TrimPercentage) || TrimPercentage < 0 || TrimPercentage >= 0.5)
            throw new InvalidOperationException("截尾比例必须在 [0, 0.5) 范围内。");
        if (WindowSize < 1)
            throw new InvalidOperationException("窗口大小必须大于 0。");
        if (MinSamplesPerEpoch < 1 || MinSamplesPerEpoch > WindowSize)
            throw new InvalidOperationException("最小样本数必须在 1 到窗口大小之间。");

        RequirePositive(DivergenceThreshold, nameof(DivergenceThreshold));
        if (!double.IsFinite(DegradedGainFactor)
            || DegradedGainFactor < 0
            || DegradedGainFactor > 1)
        {
            throw new InvalidOperationException("降 K 系数必须在 [0, 1] 范围内。");
        }
        if (DriftConfirmCycles < 1)
            throw new InvalidOperationException("漂移确认拍数必须大于 0。");
        RequireNonNegative(DriftSlopeThreshold, nameof(DriftSlopeThreshold));

        // TargetMidpoint：未配置时自动取规格中点
        if (TargetMidpoint == 0 || !double.IsFinite(TargetMidpoint))
        {
            TargetMidpoint = (UpperLimit + LowerLimit) / 2.0;
        }
        else if (TargetMidpoint < LowerLimit || TargetMidpoint > UpperLimit)
        {
            throw new InvalidOperationException(
                $"目标点 {TargetMidpoint} 必须在规格范围 [{LowerLimit}, {UpperLimit}] 内。");
        }
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
            throw new InvalidOperationException($"{name} 必须是有限数值。");
    }

    private static void RequireNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new InvalidOperationException($"{name} 必须是非负有限数值。");
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new InvalidOperationException($"{name} 必须是正有限数值。");
    }
}
