using System;

namespace CrimpHeightControl
{
    /// <summary>可重置组件（epoch HardCut 时清内部状态）。</summary>
    public interface IResettable
    {
        void Reset();
    }

    /// <summary>
    /// 控制律抽象：由偏差给出本拍设定调整量 Δ（带号；正=调高设定）。
    /// 单阻尼硬约束（§B）：实现内部平滑机制有且仅有一个。
    /// </summary>
    public interface IControlLaw
    {
        /// <param name="deviation">当前偏差 = 截尾均值 − 设定（正=偏高）。</param>
        /// <param name="effectiveGain">有效增益：DeadbandDamped 传 K×发散折减；Ewma 传发散折减（不含 K）。</param>
        /// <param name="processGain">过程增益 β。</param>
        /// <param name="maxStep">单拍步长限幅（绝对值）。</param>
        /// <returns>设定调整量 Δ，已限幅。</returns>
        double ComputeAdjustment(double deviation, double effectiveGain, double processGain, double maxStep);
    }

    /// <summary>
    /// v1 控制律：死区阻尼（仅 K 生效，§B DeadbandDamped）。Δ = effectiveGain·(−deviation/β)，再限幅。
    /// 无状态；死区判定由仲裁层负责（本类只做“纯阻尼步 + 限幅”），故可对越界偏差直接出步。
    /// </summary>
    public sealed class DampedStepControlLaw : IControlLaw
    {
        public double ComputeAdjustment(double deviation, double effectiveGain, double processGain, double maxStep)
        {
            if (processGain <= 0.0) return 0.0; // 防御：β 非法则不动作（Validate 已应拦截）
            double raw = effectiveGain * (-deviation / processGain);
            return Clamp(raw, -maxStep, maxStep);
        }

        internal static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }

    /// <summary>
    /// 【v1.5/v2 规划，未经现场验证】EWMA 控制律（仅 λ 生效，§B Ewma）。
    /// 对偏差做指数加权平滑后给出步长，<b>不再乘 K</b>（§B FR-DAMP-01：禁双阻尼）。
    /// effectiveGain 仅承载发散折减系数（Normal=1/Degraded=0.5），与 λ 平滑正交（属保守缩放，非第二重平滑）。
    /// 默认禁用；需 ControlConfig.AllowEwmaMode=true 且 ControlMode=Ewma 才会被装配。
    /// </summary>
    public sealed class EwmaControlLaw : IControlLaw, IResettable
    {
        private readonly double _lambda;
        private double _smoothed;
        private bool _initialized;

        public EwmaControlLaw(double lambda)
        {
            if (lambda <= 0.0 || lambda > 1.0) throw new ArgumentOutOfRangeException(nameof(lambda));
            _lambda = lambda;
            Reset();
        }

        public void Reset()
        {
            _smoothed = 0.0;
            _initialized = false;
        }

        public double ComputeAdjustment(double deviation, double effectiveGain, double processGain, double maxStep)
        {
            if (processGain <= 0.0) return 0.0;

            if (!_initialized)
            {
                _smoothed = deviation;
                _initialized = true;
            }
            else
            {
                _smoothed = _lambda * deviation + (1.0 - _lambda) * _smoothed;
            }

            double raw = effectiveGain * (-_smoothed / processGain);
            return DampedStepControlLaw.Clamp(raw, -maxStep, maxStep);
        }
    }
}
