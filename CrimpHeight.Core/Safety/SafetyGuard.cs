using CrimpHeight.Core.Models;
using CrimpHeight.Core.Statistics;

namespace CrimpHeight.Core.Safety;

/// <summary>
/// 安全护栏 — PRD v0.1 §9
/// 合理性阈、σ突增、数据失活检测
/// 可独立复用
/// </summary>
public class SafetyGuard
{
    private double _baselineSigma = -1;

    // 稳态 σ 滑动均值窗口，用作突增判据的基线，避免被单次极小值锁死。
    private const int BaselineWindow = 20;
    private readonly Queue<double> _baselineSamples = new();
    private double _baselineSum;

    /// <summary>
    /// 安全检查
    /// </summary>
    /// <param name="trimmedMean">当前截尾均值</param>
    /// <param name="currentSigma">当前标准差</param>
    /// <param name="lastDataTimestamp">最新数据时间戳</param>
    /// <param name="config">控制参数</param>
    /// <returns>是否触发安全护栏，以及原因</returns>
    public (bool Triggered, string Reason) Check(
        double trimmedMean,
        double currentSigma,
        DateTime? lastDataTimestamp,
        ControllerConfig config)
    {
        // 1. 合理性阈值检查
        if (trimmedMean > config.SafetyUpperBound || trimmedMean < config.SafetyLowerBound)
        {
            return (true, $"均值 {trimmedMean:F4}mm 超出合理性范围 [{config.SafetyLowerBound}, {config.SafetyUpperBound}]");
        }

        // 2. σ 突增检查
        if (_baselineSigma > 0 && currentSigma > _baselineSigma * config.SigmaSpikeFactor)
        {
            return (true, $"标准差 {currentSigma:F4} 突增，超过基线 {_baselineSigma:F4} 的 {config.SigmaSpikeFactor} 倍");
        }

        // 3. 数据失活检查
        if (lastDataTimestamp.HasValue)
        {
            var elapsed = DateTime.Now - lastDataTimestamp.Value;
            if (elapsed.TotalSeconds > config.DataStaleTimeoutSeconds)
            {
                return (true, $"数据失活 {elapsed.TotalSeconds:F0}s，超过阈值 {config.DataStaleTimeoutSeconds}s");
            }
        }

        return (false, string.Empty);
    }

    /// <summary>
    /// 更新 σ 基线（稳态时调用）。
    /// 采用最近 BaselineWindow 个稳态 σ 的滑动均值作为基线，
    /// 而非历史最小值，避免基线被单次极小 σ 锁死造成误报或漏报。
    /// </summary>
    public void UpdateBaselineSigma(double sigma)
    {
        if (!double.IsFinite(sigma) || sigma < 0) return;

        _baselineSamples.Enqueue(sigma);
        _baselineSum += sigma;

        while (_baselineSamples.Count > BaselineWindow)
            _baselineSum -= _baselineSamples.Dequeue();

        _baselineSigma = _baselineSamples.Count > 0
            ? _baselineSum / _baselineSamples.Count
            : -1;
    }

    /// <summary>
    /// 重置基线（epoch 切换时）
    /// </summary>
    public void ResetBaseline()
    {
        _baselineSigma = -1;
        _baselineSamples.Clear();
        _baselineSum = 0;
    }
}
