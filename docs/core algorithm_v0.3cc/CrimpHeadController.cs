using System;

namespace CrimpHeightControl
{
    /// <summary>
    /// 单压头控制器（编排器）。每拍调用 <see cref="ProcessCycle"/> 得到唯一仲裁决策。
    /// 数据模型：本压头有固定 productCount 个产品工位（默认 2 = P3/P4）。
    /// 维护各产品滚动窗口 + 一个“中心窗口”（每拍各产品均值）。
    /// 偏差/σ/斜率取自中心窗口；D = 各产品窗口均值偏差的极差（max−min）。
    /// 流程对应 §A：① 数据失活/原始护栏（入窗前）→ 入窗 → ② σ 突增护栏（入窗后）
    ///   → 统计 → 置信 → 发散评估 → 仲裁。
    /// 非线程安全：每个压头一个实例，单线程顺序喂拍。
    /// </summary>
    public sealed class CrimpHeadController
    {
        private readonly ControlConfig _cfg;
        private readonly int _productCount;
        private readonly SafetyGuardrail _guardrail;
        private readonly DivergenceEvaluator _divergence;
        private readonly DriftDetector _drift;
        private readonly IControlLaw _controlLaw;
        private readonly DecisionArbiter _arbiter;

        private readonly MeasurementWindow _centralWindow;
        private readonly MeasurementWindow[] _productWindows;

        private bool _hasLastTimestamp;
        private DateTime _lastTimestamp;

        /// <summary>压头标识（日志/UI 用）。</summary>
        public string HeadId { get; set; }

        /// <summary>产品工位数。</summary>
        public int ProductCount { get { return _productCount; } }

        /// <summary>中心窗口当前样本数（诊断用）。</summary>
        public int SampleCount { get { return _centralWindow.Count; } }

        /// <summary>
        /// 构造控制器。
        /// </summary>
        /// <param name="config">配置（构造时会 Validate，非法/启用 v1.5 特性将抛异常）。</param>
        /// <param name="productCount">产品工位数，默认 2（P3/P4）。</param>
        /// <param name="controlLaw">控制律；为 null 时按 ControlMode 自动装配（DeadbandDamped→K-only；Ewma→λ-only）。</param>
        /// <param name="driftDetector">漂移判定器；为 null 时新建。</param>
        public CrimpHeadController(
            ControlConfig config,
            int productCount = 2,
            IControlLaw controlLaw = null,
            DriftDetector driftDetector = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (productCount < 1) throw new ArgumentOutOfRangeException(nameof(productCount), "productCount 必须 ≥ 1。");

            config.Validate();

            _cfg = config;
            _productCount = productCount;
            _guardrail = new SafetyGuardrail(config);
            _divergence = new DivergenceEvaluator(config);
            _drift = driftDetector ?? new DriftDetector(config);
            _controlLaw = controlLaw ?? CreateDefaultControlLaw(config);
            _arbiter = new DecisionArbiter(config, _controlLaw, _drift);

            _centralWindow = new MeasurementWindow(config.WindowSize, config.TrimFraction);
            _productWindows = new MeasurementWindow[productCount];
            for (int i = 0; i < productCount; i++)
                _productWindows[i] = new MeasurementWindow(config.WindowSize, config.TrimFraction);
        }

        private static IControlLaw CreateDefaultControlLaw(ControlConfig cfg)
        {
            if (cfg.ControlMode == ControlMode.Ewma)
                return new EwmaControlLaw(cfg.EwmaLambda); // Validate 已确保 AllowEwmaMode
            return new DampedStepControlLaw();
        }

        /// <summary>
        /// epoch 重置（HardCut，§F）：清空所有窗口、漂移计数、可重置控制律状态，并丢弃时间基线。
        /// 换模 / 复标 / 工艺切换后调用；之后会自动进入 WarmUp 直至样本达闸。
        /// </summary>
        public void ResetEpoch()
        {
            _centralWindow.Clear();
            for (int i = 0; i < _productWindows.Length; i++)
                _productWindows[i].Clear();

            _drift.Reset();

            IResettable r = _controlLaw as IResettable;
            if (r != null) r.Reset();

            _hasLastTimestamp = false;
        }

        /// <summary>处理一拍，返回唯一仲裁决策。</summary>
        public ControlDecision ProcessCycle(CrimpCycleInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // 时间间隔（自上一“收到的”拍；每拍都更新基线）
            double secondsSinceLast = _hasLastTimestamp
                ? (input.Timestamp - _lastTimestamp).TotalSeconds
                : 0.0;
            _lastTimestamp = input.Timestamp;
            _hasLastTimestamp = true;

            double[] heights = input.ProductHeights;

            // 1) 产品数匹配（控制器层）→ 原始护栏（入窗前）
            GuardrailResult guard;
            if (heights == null || heights.Length != _productCount)
            {
                int got = heights == null ? 0 : heights.Length;
                guard = GuardrailResult.Outlier(
                    "数据失活：本拍产品数=" + got + "，期望 " + _productCount + "。");
            }
            else
            {
                guard = _guardrail.CheckRawSample(heights, secondsSinceLast);
            }

            // 2) 入窗（仅护栏通过：坏值不入窗）
            bool windowed = false;
            if (!guard.IsOutlier)
            {
                double cycleMean = 0.0;
                for (int i = 0; i < _productCount; i++) cycleMean += heights[i];
                cycleMean /= _productCount;

                _centralWindow.Add(cycleMean);
                for (int i = 0; i < _productCount; i++)
                    _productWindows[i].Add(heights[i]);
                windowed = true;
            }

            // 3) 统计（中心窗口；未入窗则给安全缺省）
            double estimatedDeviation = 0.0;
            double sigma = 0.0;
            double slope = 0.0;
            int n = 0;
            int effectiveCount = 0;
            double[] productDeviations = new double[_productCount];
            double dSpread = 0.0;

            if (windowed)
            {
                double trimmedMean = _centralWindow.TrimmedMean(out effectiveCount);
                estimatedDeviation = trimmedMean - _cfg.Setpoint;
                sigma = _centralWindow.StdDev();
                slope = _centralWindow.Slope();
                n = _centralWindow.Count;

                double min = double.PositiveInfinity;
                double max = double.NegativeInfinity;
                for (int i = 0; i < _productCount; i++)
                {
                    double dev = _productWindows[i].Mean() - _cfg.Setpoint;
                    productDeviations[i] = dev;
                    if (dev < min) min = dev;
                    if (dev > max) max = dev;
                }
                dSpread = max - min; // ≥ 0
            }

            // 4) σ 突增护栏（入窗后）；与原始护栏合并为单一 Guardrail 输入
            if (!guard.IsOutlier && windowed)
            {
                GuardrailResult statGuard = _guardrail.CheckStatistics(sigma, n);
                if (statGuard.IsOutlier) guard = statGuard;
            }

            // 5) 置信：样本达闸且截尾未退化
            bool confident = windowed
                             && n >= _cfg.MinSamplesPerEpoch
                             && effectiveCount > 0;

            // 6) 发散评估
            DivergenceResult divergence = _divergence.Evaluate(dSpread);

            // 7) 仲裁（唯一决策出口）
            ArbitrationInputs ai = new ArbitrationInputs(
                guard, confident, divergence, estimatedDeviation,
                productDeviations, slope, sigma, n);

            return _arbiter.Decide(ai);
        }
    }
}
