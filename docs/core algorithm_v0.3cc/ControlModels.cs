using System;
using System.Globalization;

namespace CrimpHeightControl
{
    /// <summary>一拍输入：某压头本拍各产品测量高度 + 时间戳。</summary>
    public sealed class CrimpCycleInput
    {
        /// <summary>本拍各产品测量高度（长度须等于控制器 productCount，否则判数据失活）。</summary>
        public double[] ProductHeights { get; }
        /// <summary>本拍时间戳（用于数据失活/间隔判定）。</summary>
        public DateTime Timestamp { get; }

        public CrimpCycleInput(double[] productHeights, DateTime timestamp)
        {
            ProductHeights = productHeights;
            Timestamp = timestamp;
        }

        /// <summary>时间戳缺省取 UtcNow。</summary>
        public CrimpCycleInput(double[] productHeights)
            : this(productHeights, DateTime.UtcNow)
        {
        }

        /// <summary>单产品便捷构造。</summary>
        public CrimpCycleInput(double singleHeight, DateTime timestamp)
            : this(new double[] { singleHeight }, timestamp)
        {
        }
    }

    /// <summary>
    /// 仲裁输入：由控制器在采集/统计/护栏/发散评估后整理，喂给 <see cref="DecisionArbiter"/>。
    /// 这是 §A 仲裁所需的全部判据，仲裁层本身不再触碰窗口/原始样本。
    /// </summary>
    public sealed class ArbitrationInputs
    {
        /// <summary>护栏结果（原始 + σ 突增合并）。</summary>
        public GuardrailResult Guardrail { get; }
        /// <summary>置信是否充足（样本达闸且截尾未退化）。</summary>
        public bool ConfidenceSufficient { get; }
        /// <summary>发散评估结果。</summary>
        public DivergenceResult Divergence { get; }
        /// <summary>中心偏差估计 = 截尾均值 − 设定。</summary>
        public double EstimatedDeviation { get; }
        /// <summary>各产品偏差（产品窗口均值 − 设定）。用于“任一产品越界”判据。</summary>
        public double[] ProductDeviations { get; }
        /// <summary>中心窗口斜率（DriftScore）。</summary>
        public double Slope { get; }
        /// <summary>中心窗口标准差。</summary>
        public double Sigma { get; }
        /// <summary>中心窗口样本数 n。</summary>
        public int SampleCount { get; }

        public ArbitrationInputs(
            GuardrailResult guardrail,
            bool confidenceSufficient,
            DivergenceResult divergence,
            double estimatedDeviation,
            double[] productDeviations,
            double slope,
            double sigma,
            int sampleCount)
        {
            Guardrail = guardrail;
            ConfidenceSufficient = confidenceSufficient;
            Divergence = divergence;
            EstimatedDeviation = estimatedDeviation;
            ProductDeviations = productDeviations;
            Slope = slope;
            Sigma = sigma;
            SampleCount = sampleCount;
        }
    }

    /// <summary>
    /// 仲裁产出：每拍每压头唯一的 (状态, 动作) + 诊断信息（§A FR-ARB-01）。
    /// 诊断字段由库内部填充（internal set）。
    /// </summary>
    public sealed class ControlDecision
    {
        /// <summary>状态（四态 + Diverged 覆盖态）。</summary>
        public ControlState State { get; }
        /// <summary>动作类型。</summary>
        public ControlActionType Action { get; }
        /// <summary>设定调整量 Δ（仅 Adjust 时有值；正=调高设定）。</summary>
        public double? Adjustment { get; }
        /// <summary>是否处于 degraded 发散区（已降 K / UI 标 Degraded）。</summary>
        public bool IsDegraded { get; }
        /// <summary>人类可读说明（UI / 审计日志用）。</summary>
        public string Message { get; }

        // ---- 诊断（internal set，库内填充）----
        /// <summary>本拍发散评估（可空：护栏短路时可能未评估）。</summary>
        public DivergenceResult? Divergence { get; internal set; }
        /// <summary>中心偏差估计。</summary>
        public double EstimatedDeviation { get; internal set; }
        /// <summary>D = 产品偏差极差。</summary>
        public double D { get; internal set; }
        /// <summary>中心窗口标准差。</summary>
        public double Sigma { get; internal set; }
        /// <summary>中心窗口样本数。</summary>
        public int SampleCount { get; internal set; }
        /// <summary>漂移判定（仅在到达仲裁第 4 条时有值）。</summary>
        public DriftResult? Drift { get; internal set; }

        public ControlDecision(
            ControlState state,
            ControlActionType action,
            double? adjustment,
            bool isDegraded,
            string message)
        {
            State = state;
            Action = action;
            Adjustment = adjustment;
            IsDegraded = isDegraded;
            Message = message;
        }

        public override string ToString()
        {
            string adj = Adjustment.HasValue
                ? Adjustment.Value.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture)
                : "—";
            return string.Format(CultureInfo.InvariantCulture,
                "[状态={0} 动作={1} Δ={2} 降K={3} dev={4:F4} D={5:F4} σ={6:F4} n={7}] {8}",
                State, Action, adj, IsDegraded ? "是" : "否",
                EstimatedDeviation, D, Sigma, SampleCount, Message);
        }
    }
}
