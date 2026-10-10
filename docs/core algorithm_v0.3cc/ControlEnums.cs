using System;

namespace CrimpHeightControl
{
    /// <summary>
    /// 压头控制状态（PRD §C 四状态机 + §A 仲裁覆盖态）。
    /// 估计态：WarmUp / Stable / Drift；Outlier 为安全护栏态；Diverged 为仲裁覆盖态（见 §A）。
    /// </summary>
    public enum ControlState
    {
        /// <summary>置信不足 / 刚换模 / epoch 刚重置：不动作，UI 明示“采集中，置信不足”。严禁混入 Stable。</summary>
        WarmUp = 0,

        /// <summary>置信充足、偏差在死区内、且无确认漂移：不动作。</summary>
        Stable = 1,

        /// <summary>置信充足、偏差越界或 DriftScore 确认：阻尼修正（§B）。</summary>
        Drift = 2,

        /// <summary>发散过大 |D|&gt;2T：单压头无法兼顾，停止居中 + 维护报警（仲裁覆盖态，§A 第 3 条）。</summary>
        Diverged = 3,

        /// <summary>安全护栏触发（超合理性阈 / σ 突增 / 数据失活）：停止 + 报警（复用 v0.1 §9）。</summary>
        Outlier = 4
    }

    /// <summary>本拍对 PLC 的动作类型（与 ControlState 一一对应由仲裁产出）。</summary>
    public enum ControlActionType
    {
        /// <summary>不动作（Stable / WarmUp）。</summary>
        None = 0,

        /// <summary>阻尼修正一步（Drift）。</summary>
        Adjust = 1,

        /// <summary>停止 / 停机（Outlier / Diverged）。</summary>
        Halt = 2
    }

    /// <summary>发散三级（PRD §D，替换 v0.2 binary stop）。D = max(产品偏差) − min(产品偏差)。</summary>
    public enum DivergenceLevel
    {
        /// <summary>|D| &lt; 1·T：正常居中，K×1。</summary>
        Normal = 0,

        /// <summary>1·T ≤ |D| ≤ 2·T：仍居中但降 K（更保守），UI 标 Degraded。</summary>
        Degraded = 1,

        /// <summary>|D| &gt; 2·T：停止居中 + 维护报警（最差产品偏移 |D|/2 &gt; T）。</summary>
        Stop = 2
    }

    /// <summary>
    /// 控制律模式（PRD §B 单阻尼硬约束）。同一路径平滑机制有且仅有一个生效。
    /// v1 默认 DeadbandDamped（仅 K 生效）；Ewma 为 v1.5/v2 规划（仅 λ 生效，不再乘 K），默认禁用。
    /// </summary>
    public enum ControlMode
    {
        /// <summary>仅 K 生效，无 EWMA。Δ = K·(−Deviation/β)。v1 现场推荐。</summary>
        DeadbandDamped = 0,

        /// <summary>仅 λ 生效，EWMA 输出直接作为设定，不再乘 K。v1.5/v2 规划，默认禁用（需显式 AllowEwmaMode）。</summary>
        Ewma = 1
    }

    /// <summary>
    /// epoch 数据处理模式（PRD §F）。v1 默认 HardCut（β-无关，最稳）。
    /// BiasCorrectedDecay 绑 β 置信，整体推 v1.5，v1 不引入 β 依赖（Validate 将拒绝）。
    /// </summary>
    public enum EpochDataMode
    {
        /// <summary>硬切：epoch 重置即清空窗口重新采集，与 β 无关。v1 默认。</summary>
        HardCut = 0,

        /// <summary>偏差校正衰减：绑 β 新鲜度，β 过期回落 HardCut。v1.5 规划，v1 不启用。</summary>
        BiasCorrectedDecay = 1
    }
}
