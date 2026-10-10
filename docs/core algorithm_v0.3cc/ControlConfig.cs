using System;

namespace CrimpHeightControl
{
    /// <summary>
    /// 控制器配置。所有量纲默认按 mm 处理（设定/偏差/容差/步长），时间按秒。
    /// 默认值取自 PRD §H 建议占位；🟡 项须由“锁压头采 σ/基线”前置任务实测后定。
    /// 调用 <see cref="Validate"/> 校验参数并拒绝 v1 范围外（§K）尚未启用的特性。
    /// </summary>
    public sealed class ControlConfig
    {
        // ---- 控制律主体（沿用 v0.2 §B；§B 硬化为单阻尼）----

        /// <summary>阻尼系数 K（沿用 v0.2 ControllerGain）。0&lt;K≤1 越小越保守。</summary>
        public double ControllerGain { get; set; } = 0.5;

        /// <summary>过程增益 β：设定变化 → 高度变化的比例（沿用 v0.2 FR-CTRL-01~03）。Δ = K·(−Deviation/β)。</summary>
        public double ProcessGain { get; set; } = 1.0;

        /// <summary>设定中心（目标压接高度）。偏差 = 截尾均值 − Setpoint。</summary>
        public double Setpoint { get; set; } = 0.0;

        /// <summary>容差 T（规格半带宽）。同时作为 §D 发散分级的基准（1T/2T）。</summary>
        public double Tolerance { get; set; } = 0.02;

        /// <summary>死区半宽：|偏差|≤Deadband 视为不需要动作。死区与阻尼正交（§B FR-DAMP-02），非第二重阻尼。</summary>
        public double Deadband { get; set; } = 0.005;

        /// <summary>单拍最大步长限幅（~0.01mm，沿用 v0.2）。配合二阶小量论证（§F）。</summary>
        public double MaxStepPerAdjust { get; set; } = 0.01;

        // ---- 状态机 / 置信（§C / §F）----

        /// <summary>每 epoch 进入估计态所需最小有效样本数（WarmUp 闸，FR-STATE-05 / FR-CTRL-07a）。</summary>
        public int MinSamplesPerEpoch { get; set; } = 30;

        // ---- 发散分级（§D）----

        /// <summary>degraded 区 K 折减系数（FR-DIV-03a，🟡 建议 0.5，即该区 K←0.5K）。</summary>
        public double DegradedGainFactor { get; set; } = 0.5;

        /// <summary>
        /// 发散分级基准 T。0 表示沿用 <see cref="Tolerance"/>（推荐，避免双源不一致）；
        /// 如需独立标定可设 &gt;0（沿用 v0.2 DivergenceThreshold 概念）。
        /// </summary>
        public double DivergenceThreshold { get; set; } = 0.0;

        // ---- DriftScore 抗噪（§E）----

        /// <summary>方向一致确认拍数 N（FR-STATE-03，🟡 建议 3~5）。</summary>
        public int DriftConfirmCycles { get; set; } = 4;

        /// <summary>趋势幅度显著性倍数 c（× σ/√n，FR-STATE-03，🟡 建议 2~3）。</summary>
        public double DriftNoiseFactor { get; set; } = 2.5;

        /// <summary>
        /// 是否启用幅度地板门（|slope·窗口跨度| &gt; c·σ/√n）。
        /// true = §E 双条件（方向+幅度，AC-23 抗振所需）；false = §K v1 基础版“仅连续 N 拍同向”。
        /// </summary>
        public bool UseDriftMagnitudeGate { get; set; } = true;

        // ---- 窗口（§G，v1 固定窗口）----

        /// <summary>固定窗口长度 n（FR-WINDOW-01，🟡 建议 200~300，实测后定）。双窗口为 v1.5。</summary>
        public int WindowSize { get; set; } = 250;

        /// <summary>截尾均值单侧截尾比例（FR-ANALYSIS 截尾均值），[0,0.5)。</summary>
        public double TrimFraction { get; set; } = 0.1;

        // ---- 安全护栏（复用 v0.1 §9 / FR-SAFETY-01~07）----

        /// <summary>合理性下限。低于此判 Outlier（数据失活）。默认 −∞（不约束，须实测后设）。</summary>
        public double MinReasonableHeight { get; set; } = double.NegativeInfinity;

        /// <summary>合理性上限。高于此判 Outlier。默认 +∞（不约束）。</summary>
        public double MaxReasonableHeight { get; set; } = double.PositiveInfinity;

        /// <summary>σ 突增上限。窗口 σ 超过此值判 Outlier。默认 +∞（不约束，须实测后设）。</summary>
        public double MaxSigma { get; set; } = double.PositiveInfinity;

        /// <summary>相邻有效拍最大时间间隔（秒）。&gt;0 时超间隔判数据失活；默认 0=不约束。</summary>
        public double MaxSampleGapSeconds { get; set; } = 0.0;

        // ---- v1.5/v2 特性开关（默认关闭，Validate 守门）----

        /// <summary>epoch 模式。v1 仅支持 HardCut；BiasCorrectedDecay 为 v1.5，Validate 将拒绝。</summary>
        public EpochDataMode EpochDataMode { get; set; } = EpochDataMode.HardCut;

        /// <summary>控制模式。v1 仅 DeadbandDamped；Ewma 需同时 <see cref="AllowEwmaMode"/>=true 才放行。</summary>
        public ControlMode ControlMode { get; set; } = ControlMode.DeadbandDamped;

        /// <summary>EWMA 平滑系数 λ（仅 Ewma 模式用），(0,1]。v1.5/v2。</summary>
        public double EwmaLambda { get; set; } = 0.2;

        /// <summary>显式放行 EWMA（v1.5/v2，未经现场验证）。默认 false。</summary>
        public bool AllowEwmaMode { get; set; } = false;

        /// <summary>
        /// 校验配置。范围非法抛 <see cref="ArgumentOutOfRangeException"/>；
        /// 启用 v1 范围外特性（BiasCorrectedDecay / 未放行的 Ewma）抛 <see cref="NotSupportedException"/>（§K）。
        /// </summary>
        public void Validate()
        {
            if (ProcessGain <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(ProcessGain), "过程增益 β 必须 > 0。");
            if (ControllerGain < 0.0 || ControllerGain > 1.0)
                throw new ArgumentOutOfRangeException(nameof(ControllerGain), "阻尼系数 K 应在 [0,1]。");
            if (Tolerance <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(Tolerance), "容差 T 必须 > 0。");
            if (Deadband < 0.0)
                throw new ArgumentOutOfRangeException(nameof(Deadband), "死区不能为负。");
            if (MaxStepPerAdjust <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(MaxStepPerAdjust), "单拍最大步长必须 > 0。");
            if (MinSamplesPerEpoch < 1)
                throw new ArgumentOutOfRangeException(nameof(MinSamplesPerEpoch), "MinSamplesPerEpoch 必须 ≥ 1。");
            if (DegradedGainFactor <= 0.0 || DegradedGainFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(DegradedGainFactor), "DegradedGainFactor 应在 (0,1]。");
            if (DivergenceThreshold < 0.0)
                throw new ArgumentOutOfRangeException(nameof(DivergenceThreshold), "DivergenceThreshold 不能为负（0=沿用 Tolerance）。");
            if (DriftConfirmCycles < 1)
                throw new ArgumentOutOfRangeException(nameof(DriftConfirmCycles), "DriftConfirmCycles 必须 ≥ 1。");
            if (DriftNoiseFactor < 0.0)
                throw new ArgumentOutOfRangeException(nameof(DriftNoiseFactor), "DriftNoiseFactor 不能为负。");
            if (WindowSize < 1)
                throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize 必须 ≥ 1。");
            if (TrimFraction < 0.0 || TrimFraction >= 0.5)
                throw new ArgumentOutOfRangeException(nameof(TrimFraction), "TrimFraction 应在 [0,0.5)。");
            if (MinReasonableHeight > MaxReasonableHeight)
                throw new ArgumentOutOfRangeException(nameof(MinReasonableHeight), "合理性下限不得高于上限。");
            if (MaxSampleGapSeconds < 0.0)
                throw new ArgumentOutOfRangeException(nameof(MaxSampleGapSeconds), "MaxSampleGapSeconds 不能为负。");

            if (EpochDataMode == EpochDataMode.BiasCorrectedDecay)
                throw new NotSupportedException(
                    "BiasCorrectedDecay 为 v1.5 规划（绑 β 置信）。§K 规定 v1 不引入 β 依赖，请使用 HardCut。");

            if (ControlMode == ControlMode.Ewma)
            {
                if (!AllowEwmaMode)
                    throw new NotSupportedException(
                        "Ewma 为 v1.5/v2 规划且未经现场验证。§K 规定 v1 用 DeadbandDamped（K-only）。" +
                        "如确需试验，请显式置 AllowEwmaMode=true。");
                if (EwmaLambda <= 0.0 || EwmaLambda > 1.0)
                    throw new ArgumentOutOfRangeException(nameof(EwmaLambda), "EwmaLambda 应在 (0,1]。");
            }
        }

        /// <summary>取发散分级基准 T：DivergenceThreshold&gt;0 则用之，否则沿用 Tolerance。</summary>
        public double EffectiveDivergenceTolerance()
        {
            return DivergenceThreshold > 0.0 ? DivergenceThreshold : Tolerance;
        }
    }
}
