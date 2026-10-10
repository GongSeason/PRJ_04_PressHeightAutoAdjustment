using CrimpHeight.Core.Models;

namespace CrimpHeight.Core.Controller;

/// <summary>
/// 发散分级器 — PRD §D
/// D = d_P1 - d_P2（两产品偏差差值）
/// 可独立复用
/// </summary>
public class DivergenceGrader
{
    /// <summary>
    /// 评估发散等级
    /// </summary>
    /// <param name="product1TrimmedMean">产品1截尾均值</param>
    /// <param name="product2TrimmedMean">产品2截尾均值</param>
    /// <param name="config">控制参数</param>
    /// <returns>发散等级和 D 值</returns>
    public (DivergenceLevel Level, double D) Evaluate(
        double product1TrimmedMean,
        double product2TrimmedMean,
        ControllerConfig config)
    {
        // D = 两产品截尾均值之差
        double d = product1TrimmedMean - product2TrimmedMean;
        double absD = Math.Abs(d);
        double t = config.DivergenceThreshold;

        if (absD > 2 * t)
        {
            return (DivergenceLevel.Stop, d);  // PRD §D: |D| > 2T → 停机
        }

        if (absD >= t)
        {
            return (DivergenceLevel.Degraded, d);  // T ≤ |D| ≤ 2T → 降级
        }

        return (DivergenceLevel.Normal, d);  // |D| < T → 正常
    }
}
