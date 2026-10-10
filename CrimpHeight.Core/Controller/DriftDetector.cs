using CrimpHeight.Core.Statistics;

namespace CrimpHeight.Core.Controller;

/// <summary>
/// 漂移检测器 — PRD §E DriftScore
/// v1.1：连续 N 拍 slope 同号 且 |slope| 超门限 才判 Drift
/// 可独立复用
/// </summary>
public class DriftDetector
{
    private readonly List<double> _slopeHistory = new();

    /// <summary>
    /// 记录新的 slope 值并判断是否触发 Drift
    /// </summary>
    /// <param name="recentValues">最近一个窗口的数据</param>
    /// <param name="driftConfirmCycles">连续同向确认拍数 N</param>
    /// <param name="slopeThreshold">slope 最小幅度门限 (mm/拍)</param>
    /// <returns>是否确认 Drift</returns>
    public bool IsDrifting(
        IReadOnlyList<double> recentValues,
        int driftConfirmCycles,
        double slopeThreshold = 0,
        bool recordCycle = true)
    {
        if (driftConfirmCycles < 1)
            throw new ArgumentOutOfRangeException(nameof(driftConfirmCycles), "漂移确认拍数必须大于 0。");
        if (!double.IsFinite(slopeThreshold) || slopeThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(slopeThreshold), "漂移斜率门限必须是非负有限数值。");
        if (recentValues.Count < 3) return false;

        double slope = TrimmedMeanCalculator.CalculateSlope(recentValues);
        if (recordCycle)
            _slopeHistory.Add(slope);

        // 保留最近 driftConfirmCycles × 2 的历史
        while (_slopeHistory.Count > driftConfirmCycles * 2)
            _slopeHistory.RemoveAt(0);

        if (_slopeHistory.Count < driftConfirmCycles)
            return false;

        // 检查最近 N 拍是否同号
        var recent = _slopeHistory.TakeLast(driftConfirmCycles).ToList();
        bool allPositive = recent.All(s => s > 0);
        bool allNegative = recent.All(s => s < 0);
        bool directionOk = allPositive || allNegative;

        if (!directionOk) return false;

        // 幅度门限：最近 N 拍的平均 |slope| 必须超过阈值
        if (slopeThreshold > 0)
        {
            double avgAbsSlope = recent.Average(s => Math.Abs(s));
            return avgAbsSlope >= slopeThreshold;
        }

        return true;
    }

    /// <summary>
    /// 重置历史（epoch 切换时调用）
    /// </summary>
    public void Reset() => _slopeHistory.Clear();
}
