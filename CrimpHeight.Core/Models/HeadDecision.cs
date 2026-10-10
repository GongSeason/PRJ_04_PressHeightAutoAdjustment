namespace CrimpHeight.Core.Models;

/// <summary>
/// 单个压头的仲裁决策结果 — PRD §A 命中即止
/// </summary>
public class HeadDecision
{
    /// <summary>判定状态</summary>
    public HeadState State { get; set; }

    /// <summary>发散等级</summary>
    public DivergenceLevel DivergenceLevel { get; set; }

    /// <summary>建议调整量 (mm)，正=压头下降，负=压头上升</summary>
    public double AdjustmentDelta { get; set; }

    /// <summary>调整方向</summary>
    public AdjustmentDirection Direction { get; set; }

    /// <summary>仲裁命中原因说明</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>是否需要 PLC 调整动作（非零调整量）</summary>
    public bool RequiresAction => State == HeadState.Drift && Math.Abs(AdjustmentDelta) > 1e-10;

    /// <summary>是否需要停机报警</summary>
    public bool RequiresStop => State == HeadState.Outlier
        || (State == HeadState.Drift && DivergenceLevel == DivergenceLevel.Stop);
}

/// <summary>
/// 单个压头的完整分析结果（统计 + 仲裁）
/// </summary>
public class HeadAnalysisResult
{
    public string HeadName { get; set; } = string.Empty;

    /// <summary>产品统计</summary>
    public ProductStats Product1 { get; set; } = new();
    public ProductStats Product2 { get; set; } = new();

    /// <summary>合并截尾均值</summary>
    public double CombinedTrimmedMean { get; set; }

    /// <summary>偏差（CombinedTrimmedMean - TargetMidpoint）</summary>
    public double Deviation { get; set; }

    /// <summary>两产品偏差差值 D = d_P1 - d_P2</summary>
    public double DivergenceD { get; set; }

    /// <summary>仲裁决策</summary>
    public HeadDecision Decision { get; set; } = new();

    /// <summary>有效样本数</summary>
    public int ValidSampleCount { get; set; }

    /// <summary>当前拍的线性回归斜率 (mm/拍)</summary>
    public double CurrentSlope { get; set; }
}

/// <summary>
/// 单个产品的统计结果
/// </summary>
public class ProductStats
{
    public string ProductName { get; set; } = string.Empty;
    public int DataCount { get; set; }
    public double Mean { get; set; }
    public double TrimmedMean { get; set; }
    public double StandardDeviation { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}
