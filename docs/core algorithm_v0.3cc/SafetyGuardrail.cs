using System;

namespace CrimpHeightControl
{
    /// <summary>安全护栏结果。默认值（IsOutlier=false）即“通过”。</summary>
    public struct GuardrailResult
    {
        /// <summary>是否判为 Outlier（触发停止 + 报警）。</summary>
        public bool IsOutlier { get; }
        /// <summary>触发原因（通过时为 null）。</summary>
        public string Reason { get; }

        public GuardrailResult(bool isOutlier, string reason)
        {
            IsOutlier = isOutlier;
            Reason = reason;
        }

        /// <summary>通过（非 Outlier）。</summary>
        public static GuardrailResult Ok()
        {
            return new GuardrailResult(false, null);
        }

        /// <summary>判 Outlier。</summary>
        public static GuardrailResult Outlier(string reason)
        {
            return new GuardrailResult(true, reason);
        }
    }

    /// <summary>
    /// 安全护栏（复用 v0.1 §9 / FR-SAFETY-01~07）。两段检查：
    /// <see cref="CheckRawSample"/> 在入窗前对原始样本做有效性/合理性/数据失活判定（坏值不入窗）；
    /// <see cref="CheckStatistics"/> 在入窗后对窗口 σ 做突增判定。
    /// 对应仲裁 §A 第 1 条（最高优先级）。
    /// </summary>
    public sealed class SafetyGuardrail
    {
        private readonly ControlConfig _cfg;

        public SafetyGuardrail(ControlConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        }

        /// <summary>
        /// 原始样本检查（入窗前）：空/NaN/Inf、超合理性阈、数据失活（采样间隔过大）。
        /// 产品数匹配由控制器负责（它知道 productCount）。
        /// </summary>
        /// <param name="heights">本拍各产品测量高度。</param>
        /// <param name="secondsSinceLastReceived">距上一“收到的”拍的时间间隔（秒）；首拍传 0。</param>
        public GuardrailResult CheckRawSample(double[] heights, double secondsSinceLastReceived)
        {
            if (heights == null || heights.Length == 0)
                return GuardrailResult.Outlier("数据失活：本拍无测量值。");

            for (int i = 0; i < heights.Length; i++)
            {
                double h = heights[i];
                if (double.IsNaN(h) || double.IsInfinity(h))
                    return GuardrailResult.Outlier("数据失活：测量值非有限数（位置 " + i + "）。");
                if (h < _cfg.MinReasonableHeight || h > _cfg.MaxReasonableHeight)
                    return GuardrailResult.Outlier(
                        "超合理性阈：测量值 " + h.ToString("G6") + " 越出 [" +
                        _cfg.MinReasonableHeight.ToString("G6") + ", " +
                        _cfg.MaxReasonableHeight.ToString("G6") + "]（位置 " + i + "）。");
            }

            if (_cfg.MaxSampleGapSeconds > 0.0 && secondsSinceLastReceived > _cfg.MaxSampleGapSeconds)
                return GuardrailResult.Outlier(
                    "数据失活：采样间隔 " + secondsSinceLastReceived.ToString("G4") +
                    "s 超阈 " + _cfg.MaxSampleGapSeconds.ToString("G4") + "s。");

            return GuardrailResult.Ok();
        }

        /// <summary>
        /// 统计量检查（入窗后）：σ 突增。样本数 &lt; 2 或 MaxSigma=+∞ 时视为通过。
        /// </summary>
        public GuardrailResult CheckStatistics(double sigma, int sampleCount)
        {
            if (sampleCount < 2) return GuardrailResult.Ok();
            if (double.IsInfinity(_cfg.MaxSigma)) return GuardrailResult.Ok();
            if (double.IsNaN(sigma) || double.IsInfinity(sigma))
                return GuardrailResult.Outlier("σ 异常：非有限数。");
            if (sigma > _cfg.MaxSigma)
                return GuardrailResult.Outlier(
                    "σ 突增：σ=" + sigma.ToString("G4") + " 超阈 " + _cfg.MaxSigma.ToString("G4") + "。");
            return GuardrailResult.Ok();
        }
    }
}
