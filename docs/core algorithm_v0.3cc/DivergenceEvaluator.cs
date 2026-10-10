using System;

namespace CrimpHeightControl
{
    /// <summary>发散评估结果（PRD §D）。</summary>
    public struct DivergenceResult
    {
        /// <summary>D = max(产品偏差) − min(产品偏差)（带号差的极差，非负）。</summary>
        public double D { get; }
        /// <summary>发散等级。</summary>
        public DivergenceLevel Level { get; }
        /// <summary>该等级下 K 的折减系数：Normal=1，Degraded=DegradedGainFactor，Stop=0。</summary>
        public double GainFactor { get; }

        public DivergenceResult(double d, DivergenceLevel level, double gainFactor)
        {
            D = d;
            Level = level;
            GainFactor = gainFactor;
        }
    }

    /// <summary>
    /// 发散三级评估（PRD §D，替换 v0.2 binary stop）。基准 T 取 <see cref="ControlConfig.EffectiveDivergenceTolerance"/>：
    /// |D|&lt;1T → Normal（K×1）；1T≤|D|≤2T → Degraded（K×DegradedGainFactor，仍居中）；|D|&gt;2T → Stop（停止居中）。
    /// 注：传入的 D 应为各产品偏差的极差（max−min），始终 ≥ 0。
    /// </summary>
    public sealed class DivergenceEvaluator
    {
        private readonly ControlConfig _cfg;

        public DivergenceEvaluator(ControlConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        }

        public DivergenceResult Evaluate(double d)
        {
            double absD = Math.Abs(d);
            double t = _cfg.EffectiveDivergenceTolerance();

            if (absD > 2.0 * t)
                return new DivergenceResult(d, DivergenceLevel.Stop, 0.0);
            if (absD >= 1.0 * t)
                return new DivergenceResult(d, DivergenceLevel.Degraded, _cfg.DegradedGainFactor);
            return new DivergenceResult(d, DivergenceLevel.Normal, 1.0);
        }
    }
}
