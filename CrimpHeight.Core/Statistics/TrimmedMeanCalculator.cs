namespace CrimpHeight.Core.Statistics;

/// <summary>
/// 统计计算工具 — 截尾均值、标准差、Cpk、线性回归斜率
/// 可独立复用，无外部依赖
/// </summary>
public static class TrimmedMeanCalculator
{
    /// <summary>
    /// 计算截尾均值 — 去掉头尾 trimPercentage 后求平均
    /// </summary>
    public static double CalculateTrimmedMean(IReadOnlyList<double> values, double trimPercentage)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        int trimCount = (int)(sorted.Count * trimPercentage);

        // 数据量太少，不截尾
        if (trimCount * 2 >= sorted.Count)
            return sorted.Average();

        var trimmed = sorted.Skip(trimCount).Take(sorted.Count - trimCount * 2);
        return trimmed.Average();
    }

    /// <summary>
    /// 计算样本标准差（无偏，n-1 分母）
    /// </summary>
    public static double CalculateStdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;

        double mean = values.Average();
        double sumSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }

    /// <summary>
    /// 计算过程能力指数 Cpk
    /// Cpk = min((USL - mean) / 3σ, (mean - LSL) / 3σ)
    /// </summary>
    public static double CalculateCpk(IReadOnlyList<double> values, double upperLimit, double lowerLimit)
    {
        if (values.Count < 2) return 0;

        double mean = values.Average();
        double stdDev = CalculateStdDev(values);

        if (stdDev < 1e-10) return double.MaxValue; // 零方差，完美能力

        double cpu = (upperLimit - mean) / (3 * stdDev);
        double cpl = (mean - lowerLimit) / (3 * stdDev);
        return Math.Min(cpu, cpl);
    }

    /// <summary>
    /// 计算线性回归斜率 — 用于 DriftScore
    /// 最小二乘法：slope = Σ(xi - x̄)(yi - ȳ) / Σ(xi - x̄)²
    /// </summary>
    public static double CalculateSlope(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;

        int n = values.Count;
        double xMean = (n - 1) / 2.0; // x = 0,1,2,...,n-1
        double yMean = values.Average();

        double numerator = 0;
        double denominator = 0;

        for (int i = 0; i < n; i++)
        {
            double dx = i - xMean;
            numerator += dx * (values[i] - yMean);
            denominator += dx * dx;
        }

        return Math.Abs(denominator) < 1e-10 ? 0 : numerator / denominator;
    }
}
