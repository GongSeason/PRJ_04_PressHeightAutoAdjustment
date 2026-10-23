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

    /// <summary>
    /// 数据产生间隔（秒）— 相机每隔多少秒写入一条数据。
    /// 用于校验刷新间隔是否足够推进漂移检测拍。
    /// 约束：RefreshInterval / DataInterval ≥ MinSamplesPerEpoch / 2
    /// 即每次刷新的新增数据条数必须 ≥ 漂移拍推进门限。
    /// </summary>
    public double DataIntervalSeconds { get; set; } = 0.5;

    // === 自适应增 K 参数 ===

    /// <summary>连续停滞多少次后开始增 K</summary>
    public int AdaptiveKStallThreshold { get; set; } = 3;

    /// <summary>每次停滞后 K 的升档步进</summary>
    public double AdaptiveKBoostStep { get; set; } = 0.1;

    /// <summary>K 的绝对上限（自适应增 K 不会超过此值）</summary>
    public double AdaptiveKMaxLimit { get; set; } = 0.8;

    /// <summary>偏差幅度变化率低于此值视为收敛停滞（0.3 = 30%）</summary>
    public double AdaptiveKStallChangeRatio { get; set; } = 0.3;

    // === 漂移拍推进 ===

    /// <summary>漂移拍推进最小门限 — 每次刷新新增样本数至少达到此值才推进一拍</summary>
    public int DriftSampleStrideMin { get; set; } = 10;

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
        RequirePositive(DataIntervalSeconds, nameof(DataIntervalSeconds));

        // 自适应增 K 参数校验
        if (AdaptiveKStallThreshold < 1)
            throw new InvalidOperationException("自适应增K停滞阈值必须大于 0。");
        RequirePositive(AdaptiveKBoostStep, nameof(AdaptiveKBoostStep));
        if (!double.IsFinite(AdaptiveKMaxLimit) || AdaptiveKMaxLimit <= 0 || AdaptiveKMaxLimit > 1)
            throw new InvalidOperationException("自适应增K上限必须在 (0, 1] 范围内。");
        if (AdaptiveKMaxLimit <= ControllerGain)
            throw new InvalidOperationException(
                $"自适应增K上限 ({AdaptiveKMaxLimit}) 必须大于基础增益 K ({ControllerGain})，否则增K无意义。");
        if (!double.IsFinite(AdaptiveKStallChangeRatio) || AdaptiveKStallChangeRatio <= 0 || AdaptiveKStallChangeRatio >= 1)
            throw new InvalidOperationException("停滞变化率阈值必须在 (0, 1) 范围内。");

        // 漂移拍推进门限校验
        if (DriftSampleStrideMin < 1)
            throw new InvalidOperationException("漂移拍推进最小门限必须大于 0。");

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
