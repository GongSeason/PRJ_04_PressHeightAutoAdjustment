using System;

namespace CrimpHeightControl
{
    /// <summary>
    /// 决策仲裁层（PRD §A）。每拍每压头按固定优先级匹配，<b>命中即止，产出唯一 (state, action)</b>：
    /// <list type="number">
    /// <item>安全护栏触发 → Outlier（停止 + 报警）</item>
    /// <item>置信不足 → WarmUp（不动作）</item>
    /// <item>|D|&gt;2T（Stop 级）→ Diverged（停止居中 + 维护报警）</item>
    /// <item>任一产品越界 或 DriftScore 确认 → Adjust（阻尼修正；degraded 区降 K）</item>
    /// <item>否则 → Stable（不动作）</item>
    /// </list>
    /// FR-ARB-02：Diverged 优先于 Stable（mean≈0 但 |D| 大不得判 Stable）。
    /// FR-ARB-03：WarmUp 先于 Diverged/Adjust（样本不足时 D 与 DriftScore 不可信）。
    /// DriftDetector 仅在到达第 4 条（护栏 OK + 置信足 + 非 Stop）时更新，避免在不可信拍累计同号。
    /// </summary>
    public sealed class DecisionArbiter
    {
        private readonly ControlConfig _cfg;
        private readonly IControlLaw _controlLaw;
        private readonly DriftDetector _driftDetector;

        public DecisionArbiter(ControlConfig cfg, IControlLaw controlLaw, DriftDetector driftDetector)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _controlLaw = controlLaw ?? throw new ArgumentNullException(nameof(controlLaw));
            _driftDetector = driftDetector ?? throw new ArgumentNullException(nameof(driftDetector));
        }

        public ControlDecision Decide(ArbitrationInputs a)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));

            // 1) 安全护栏（最高优先级）
            if (a.Guardrail.IsOutlier)
            {
                return Stamp(
                    new ControlDecision(ControlState.Outlier, ControlActionType.Halt, null, false,
                        "安全护栏触发：" + (a.Guardrail.Reason ?? "未知原因")),
                    a, null);
            }

            // 2) 置信不足（FR-ARB-03）：D / DriftScore 此时不可信
            if (!a.ConfidenceSufficient)
            {
                return Stamp(
                    new ControlDecision(ControlState.WarmUp, ControlActionType.None, null, false,
                        "估计中，置信不足（样本不足 / 刚换模 / epoch 重置）。"),
                    a, null);
            }

            // 3) 发散 Stop 级：|D|>2T（FR-ARB-02，优先于 Adjust/Stable）
            if (a.Divergence.Level == DivergenceLevel.Stop)
            {
                return Stamp(
                    new ControlDecision(ControlState.Diverged, ControlActionType.Halt, null, false,
                        "发散过大 |D|=" + a.Divergence.D.ToString("F4") +
                        " > 2T，单压头无法兼顾，停止居中 + 维护报警。"),
                    a, null);
            }

            // —— 到此为：护栏 OK、置信足、非 Stop。这是唯一更新 DriftDetector 的位置。——
            DriftResult drift = _driftDetector.Update(a.Slope, a.Sigma, a.SampleCount);

            bool degraded = a.Divergence.Level == DivergenceLevel.Degraded;

            // 4) 触发动作：中心偏差越死区 或 任一产品越容差 或 漂移确认
            bool deviationTrigger = Math.Abs(a.EstimatedDeviation) > _cfg.Deadband;
            bool productTrigger = AnyProductOutOfTolerance(a.ProductDeviations);
            bool driftTrigger = drift.Confirmed;

            if (deviationTrigger || productTrigger || driftTrigger)
            {
                // 有效增益：DeadbandDamped → K×发散折减；Ewma → 仅发散折减（不含 K，§B）
                double effectiveGain = (_cfg.ControlMode == ControlMode.Ewma)
                    ? a.Divergence.GainFactor
                    : _cfg.ControllerGain * a.Divergence.GainFactor;

                double adjustment = _controlLaw.ComputeAdjustment(
                    a.EstimatedDeviation, effectiveGain, _cfg.ProcessGain, _cfg.MaxStepPerAdjust);

                string why = BuildAdjustReason(deviationTrigger, productTrigger, driftTrigger, degraded);
                return Stamp(
                    new ControlDecision(ControlState.Drift, ControlActionType.Adjust, adjustment, degraded, why),
                    a, drift);
            }

            // 5) 否则 Stable（degraded 区即便不动作也回报 Degraded，提示工位一致性）
            string stableMsg = degraded
                ? "稳定（偏差在死区内、无确认漂移），但工位一致性下降（Degraded），建议关注。"
                : "稳定：偏差在死区内且无确认漂移。";
            return Stamp(
                new ControlDecision(ControlState.Stable, ControlActionType.None, null, degraded, stableMsg),
                a, drift);
        }

        private bool AnyProductOutOfTolerance(double[] productDeviations)
        {
            if (productDeviations == null) return false;
            for (int i = 0; i < productDeviations.Length; i++)
            {
                if (Math.Abs(productDeviations[i]) > _cfg.Tolerance) return true;
            }
            return false;
        }

        private static string BuildAdjustReason(bool dev, bool prod, bool drift, bool degraded)
        {
            string baseReason;
            if (drift && (dev || prod))
                baseReason = "偏差越界且漂移确认，阻尼修正。";
            else if (drift)
                baseReason = "DriftScore 确认（连续同向且趋势显著），阻尼修正。";
            else if (prod)
                baseReason = "存在产品越容差，阻尼修正压均值。";
            else
                baseReason = "中心偏差越死区，阻尼修正。";
            return degraded ? baseReason + "（degraded 区，已降 K）" : baseReason;
        }

        private ControlDecision Stamp(ControlDecision d, ArbitrationInputs a, DriftResult? drift)
        {
            d.Divergence = a.Divergence;
            d.EstimatedDeviation = a.EstimatedDeviation;
            d.D = a.Divergence.D;
            d.Sigma = a.Sigma;
            d.SampleCount = a.SampleCount;
            d.Drift = drift;
            return d;
        }
    }
}
