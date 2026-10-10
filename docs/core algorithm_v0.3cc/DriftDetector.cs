using System;

namespace CrimpHeightControl
{
    /// <summary>漂移判定结果（PRD §E）。</summary>
    public struct DriftResult
    {
        /// <summary>是否确认漂移（按配置：双条件 或 仅方向）。</summary>
        public bool Confirmed { get; }
        /// <summary>方向条件：连续 N 拍同号是否满足。</summary>
        public bool DirectionOk { get; }
        /// <summary>幅度条件：|slope·窗口跨度| 是否超噪声地板（未启用幅度门时恒为 true）。</summary>
        public bool MagnitudeOk { get; }
        /// <summary>当前连续同号拍数。</summary>
        public int ConsecutiveSameSign { get; }
        /// <summary>本拍斜率（DriftScore）。</summary>
        public double Slope { get; }
        /// <summary>本拍斜率符号（-1/0/1）。</summary>
        public int Sign { get; }

        public DriftResult(bool confirmed, bool directionOk, bool magnitudeOk,
                           int consecutiveSameSign, double slope, int sign)
        {
            Confirmed = confirmed;
            DirectionOk = directionOk;
            MagnitudeOk = magnitudeOk;
            ConsecutiveSameSign = consecutiveSameSign;
            Slope = slope;
            Sign = sign;
        }
    }

    /// <summary>
    /// DriftScore 抗噪判定器（PRD §E）。判 Drift 须满足：
    /// ① 方向一致——连续 N(=DriftConfirmCycles) 拍 slope 同号；
    /// ② 幅度显著——|slope·(n-1)| &gt; c·(σ/√n)（c=DriftNoiseFactor）。
    /// UseDriftMagnitudeGate=false 时退化为 §K v1 基础版（仅方向）。
    /// 仅应在“置信充足且未发散停机”的拍调用 <see cref="Update"/>（见仲裁 §A 第 4 条）。
    /// </summary>
    public sealed class DriftDetector
    {
        private readonly ControlConfig _cfg;
        private int _consecutive;
        private int _lastSign;

        public DriftDetector(ControlConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            Reset();
        }

        /// <summary>重置连续同号计数（epoch 重置 / 换模）。</summary>
        public void Reset()
        {
            _consecutive = 0;
            _lastSign = 0;
        }

        /// <summary>
        /// 用本拍斜率更新并判定。
        /// </summary>
        /// <param name="slope">本拍最小二乘斜率（每拍变化量）。</param>
        /// <param name="sigma">当前窗口标准差。</param>
        /// <param name="sampleCount">当前窗口样本数 n。</param>
        public DriftResult Update(double slope, double sigma, int sampleCount)
        {
            int sign = Math.Sign(slope);

            if (sign == 0)
            {
                _consecutive = 0;
                _lastSign = 0;
            }
            else if (sign == _lastSign)
            {
                _consecutive++;
            }
            else
            {
                _consecutive = 1;
                _lastSign = sign;
            }

            bool directionOk = _consecutive >= _cfg.DriftConfirmCycles;

            // 幅度地板：趋势幅度 = |slope|·(n-1)；噪声地板 = c·σ/√n
            bool magnitudeOk;
            if (!_cfg.UseDriftMagnitudeGate)
            {
                magnitudeOk = true;
            }
            else if (sampleCount < 2 || double.IsNaN(sigma) || double.IsInfinity(sigma))
            {
                magnitudeOk = false;
            }
            else
            {
                double trend = Math.Abs(slope) * (sampleCount - 1);
                double noiseFloor = _cfg.DriftNoiseFactor * sigma / Math.Sqrt(sampleCount);
                magnitudeOk = trend > noiseFloor;
            }

            bool confirmed = directionOk && magnitudeOk;
            return new DriftResult(confirmed, directionOk, magnitudeOk, _consecutive, slope, sign);
        }
    }
}
