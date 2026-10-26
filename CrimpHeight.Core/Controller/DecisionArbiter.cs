using CrimpHeight.Core.Models;
using CrimpHeight.Core.Statistics;

namespace CrimpHeight.Core.Controller;

/// <summary>
/// 决策仲裁器 — PRD §A
/// 每拍每压头按优先级匹配，命中即止，产出唯一 (State, Action)
/// 可独立复用
/// </summary>
public class DecisionArbiter
{
    private readonly R2RController _r2r = new();
    private readonly DivergenceGrader _divGrader = new();
    private readonly DriftDetector _driftDetector = new();

    /// <summary>
    /// 对单个压头的一路高度序列执行统计与控制决策，不进行产品间发散比较。
    /// </summary>
    public HeadAnalysisResult ArbitrateSingle(
        string headName,
        IReadOnlyList<double> values,
        ControllerConfig config,
        DateTime? lastDataTimestamp,
        bool advanceDriftCycle = true)
    {
        config.Validate();
        var result = new HeadAnalysisResult { HeadName = headName };
        var validValues = values.Where(IsValidMeasurement).ToList();
        result.ValidSampleCount = validValues.Count;
        result.Product1 = CalculateStats(headName, validValues, config.TrimPercentage);
        result.CombinedTrimmedMean = result.Product1.TrimmedMean;
        result.Deviation = result.CombinedTrimmedMean - config.TargetMidpoint;

        if (validValues.Count < config.MinSamplesPerEpoch)
        {
            result.Decision = new HeadDecision
            {
                State = HeadState.WarmUp,
                DivergenceLevel = DivergenceLevel.Normal,
                Reason = $"[置信不足] 样本 {validValues.Count} < 最低要求 {config.MinSamplesPerEpoch}"
            };
            return result;
        }

        result.CurrentSlope = TrimmedMeanCalculator.CalculateSlope(validValues);
        bool outOfBounds = result.Deviation > config.UpperTolerance
                        || result.Deviation < -config.LowerTolerance;
        bool driftConfirmed = _driftDetector.IsDrifting(
            validValues, config.DriftConfirmCycles, config.DriftSlopeThreshold, advanceDriftCycle);

        if (outOfBounds || driftConfirmed)
        {
            double delta;
            if (outOfBounds)
            {
                // 越界触发：正常走控制律（含死区判断）
                delta = _r2r.ComputeAdjustment(result.CombinedTrimmedMean, config, DivergenceLevel.Normal);
            }
            else
            {
                // 漂移确认触发：绕过死区，直接 K × deviation 限幅。
                // 漂移的意义是"虽然还没越界，但趋势确认在往外跑"，应提前补偿。
                delta = _r2r.ComputeAdjustmentBypassDeadband(result.CombinedTrimmedMean, config, DivergenceLevel.Normal);
            }
            result.Decision = new HeadDecision
            {
                State = HeadState.Drift,
                DivergenceLevel = DivergenceLevel.Normal,
                AdjustmentDelta = delta,
                Direction = R2RController.GetDirection(delta),
                Reason = $"[调整] {(outOfBounds ? "偏差越界" : "DriftScore 确认")}，Δ={delta:F5}mm"
            };
            return result;
        }

        result.Decision = new HeadDecision
        {
            State = HeadState.Stable,
            DivergenceLevel = DivergenceLevel.Normal,
            Reason = "[稳定] 偏差在容差内，无漂移"
        };
        return result;
    }

    /// <summary>
    /// 执行完整仲裁流程（旧版双产品数据接口）。
    /// </summary>
    /// <param name="headName">压头名称</param>
    /// <param name="product1Values">产品1数据</param>
    /// <param name="product2Values">产品2数据</param>
    /// <param name="config">控制参数</param>
    /// <param name="lastDataTimestamp">最新数据时间</param>
    /// <returns>完整分析结果</returns>
    public HeadAnalysisResult Arbitrate(
        string headName,
        IReadOnlyList<double> product1Values,
        IReadOnlyList<double> product2Values,
        ControllerConfig config,
        DateTime? lastDataTimestamp,
        bool advanceDriftCycle = true)
    {
        config.Validate();

        var result = new HeadAnalysisResult { HeadName = headName };

        // 两个产品按拍成对过滤，避免单边无效值导致时间序列错位。
        int pairedCount = Math.Min(product1Values.Count, product2Values.Count);
        var p1 = new List<double>(pairedCount);
        var p2 = new List<double>(pairedCount);
        for (int i = 0; i < pairedCount; i++)
        {
            double p1Value = product1Values[i];
            double p2Value = product2Values[i];
            if (!IsValidMeasurement(p1Value) || !IsValidMeasurement(p2Value))
                continue;

            p1.Add(p1Value);
            p2.Add(p2Value);
        }

        int validCount = p1.Count;
        result.ValidSampleCount = validCount;

        // 统计计算
        result.Product1 = CalculateStats("产品1", p1, config.TrimPercentage);
        result.Product2 = CalculateStats("产品2", p2, config.TrimPercentage);

        // 合并截尾均值
        var combined = new List<double>(p1);
        combined.AddRange(p2);
        result.CombinedTrimmedMean = TrimmedMeanCalculator.CalculateTrimmedMean(combined, config.TrimPercentage);
        result.Deviation = result.CombinedTrimmedMean - config.TargetMidpoint;

        // 没有有效样本时统计值没有意义，不能把默认均值 0 当成安全越界。
        if (validCount == 0)
        {
            result.Decision = new HeadDecision
            {
                State = HeadState.WarmUp,
                DivergenceLevel = DivergenceLevel.Normal,
                AdjustmentDelta = 0,
                Direction = AdjustmentDirection.None,
                Reason = $"[置信不足] 样本 0 < 最低要求 {config.MinSamplesPerEpoch}"
            };
            return result;
        }

        // 发散计算
        var (divLevel, d) = _divGrader.Evaluate(
            result.Product1.TrimmedMean, result.Product2.TrimmedMean, config);
        result.DivergenceD = d;

        // ===== 仲裁流程 =====

        // 1. 置信不足
        if (validCount < config.MinSamplesPerEpoch)
        {
            result.Decision = new HeadDecision
            {
                State = HeadState.WarmUp,
                DivergenceLevel = DivergenceLevel.Normal,
                AdjustmentDelta = 0,
                Direction = AdjustmentDirection.None,
                Reason = $"[置信不足] 样本 {validCount} < 最低要求 {config.MinSamplesPerEpoch}"
            };
            return result;
        }

        // 2. 发散严重（Stop）
        if (divLevel == DivergenceLevel.Stop)
        {
            result.Decision = new HeadDecision
            {
                State = HeadState.Drift,
                DivergenceLevel = DivergenceLevel.Stop,
                AdjustmentDelta = 0,
                Direction = AdjustmentDirection.None,
                Reason = $"[发散停止] |D|={Math.Abs(d):F4}mm > 2T={2 * config.DivergenceThreshold:F4}mm，需维护"
            };
            return result;
        }

        // 3. 触发动作（越界或 DriftScore 确认）
        bool outOfBounds = result.Deviation > config.UpperTolerance
                        || result.Deviation < -config.LowerTolerance;

        // 每拍先取两个产品的中心值，既消除固定落差，又保留真实时间顺序。
        var centerValues = new List<double>(validCount);
        for (int i = 0; i < validCount; i++)
            centerValues.Add((p1[i] + p2[i]) / 2.0);

        // 计算当前拍的斜率并保存
        result.CurrentSlope = TrimmedMeanCalculator.CalculateSlope(centerValues);

        bool driftConfirmed = _driftDetector.IsDrifting(
            centerValues,
            config.DriftConfirmCycles,
            config.DriftSlopeThreshold,
            advanceDriftCycle);

        if (outOfBounds || driftConfirmed)
        {
            double delta = outOfBounds
                ? _r2r.ComputeAdjustment(result.CombinedTrimmedMean, config, divLevel)
                : _r2r.ComputeAdjustmentBypassDeadband(result.CombinedTrimmedMean, config, divLevel);
            var direction = R2RController.GetDirection(delta);

            string reasonDetail = outOfBounds ? "偏差越界" : "DriftScore 确认";
            string degradedTag = divLevel == DivergenceLevel.Degraded ? " [降级模式]" : "";

            result.Decision = new HeadDecision
            {
                State = HeadState.Drift,
                DivergenceLevel = divLevel,
                AdjustmentDelta = delta,
                Direction = direction,
                Reason = $"[调整] {reasonDetail}{degradedTag}，Δ={delta:F5}mm"
            };

            return result;
        }

        // 4. 稳定
        result.Decision = new HeadDecision
        {
            State = HeadState.Stable,
            DivergenceLevel = DivergenceLevel.Normal,
            AdjustmentDelta = 0,
            Direction = AdjustmentDirection.None,
            Reason = "[稳定] 偏差在容差内，无漂移"
        };

        return result;
    }

    /// <summary>
    /// 完全重置所有内部状态（配方切换、用户手动重置时调用）。
    /// 包括漂移历史和自适应 K 增量。
    /// </summary>
    public void Reset()
    {
        _driftDetector.Reset();
        _r2r.ResetKBoost();
    }

    /// <summary>
    /// 轻量重置：仅清除漂移历史，保留自适应 K 增量。
    /// 用于自动下发后 bypass 旧数据的场景，让增 K 学习结果跨调整周期延续。
    /// </summary>
    public void ResetKeepK()
    {
        _driftDetector.Reset();
    }

    private static ProductStats CalculateStats(string name, IReadOnlyList<double> values, double trimPercentage)
    {
        if (values.Count == 0)
            return new ProductStats { ProductName = name };

        return new ProductStats
        {
            ProductName = name,
            DataCount = values.Count,
            Mean = values.Average(),
            TrimmedMean = TrimmedMeanCalculator.CalculateTrimmedMean(values, trimPercentage),
            StandardDeviation = TrimmedMeanCalculator.CalculateStdDev(values),
            Min = values.Min(),
            Max = values.Max()
        };
    }

    private static bool IsValidMeasurement(double value) =>
        double.IsFinite(value) && value > 0;
}
