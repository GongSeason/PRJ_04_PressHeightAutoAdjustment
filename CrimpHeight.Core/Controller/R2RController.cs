using CrimpHeight.Core.Models;

namespace CrimpHeight.Core.Controller;

/// <summary>
/// R2R 控制器 — PRD §B 控制律
/// K-only (DeadbandDamped) 模式，β-无关
/// 可独立复用
/// </summary>
public class R2RController
{
    /// <summary>
    /// 计算调整量 — 核心控制律
    /// Δ = K × Deviation，带死区和步长限幅
    /// 正偏差(偏高)→压头下降，负偏差(偏低)→压头上升
    /// </summary>
    /// <param name="trimmedMean">当前截尾均值</param>
    /// <param name="config">控制参数</param>
    /// <param name="divergenceLevel">发散等级（Degraded 时降 K）</param>
    /// <returns>调整量 (mm)，0 表示不动作</returns>
    public double ComputeAdjustment(
        double trimmedMean,
        ControllerConfig config,
        DivergenceLevel divergenceLevel)
    {
        config.Validate();

        // 目标点 = 规格中点
        double target = config.TargetMidpoint;
        double deviation = trimmedMean - target;

        // 死区判断：偏差在容差范围内，不动作
        if (deviation >= -config.LowerTolerance
            && deviation <= config.UpperTolerance)
        {
            return 0;
        }

        // 确定有效 K
        double effectiveK = config.ControllerGain;
        if (divergenceLevel == DivergenceLevel.Degraded)
        {
            effectiveK *= config.DegradedGainFactor;
        }

        // R2R 控制律: Δ = K × Deviation（正偏差→压头下降，负偏差→压头上升）
        double rawDelta = effectiveK * deviation;

        // 步长限幅
        double clampedDelta = Math.Clamp(rawDelta, -config.MaxStepPerAdjust, config.MaxStepPerAdjust);

        return clampedDelta;
    }

    /// <summary>
    /// 计算调整量 — 绕过死区版本，用于漂移确认触发。
    /// 漂移确认意味着趋势已确认在往外跑，即使偏差还在死区内也应提前补偿。
    /// Δ = K × Deviation，仅做步长限幅，不做死区判断。
    /// </summary>
    public double ComputeAdjustmentBypassDeadband(
        double trimmedMean,
        ControllerConfig config,
        DivergenceLevel divergenceLevel)
    {
        config.Validate();

        double target = config.TargetMidpoint;
        double deviation = trimmedMean - target;

        // 偏差为零时不动作（精确在目标点上无需调整）
        if (Math.Abs(deviation) < 1e-10)
            return 0;

        double effectiveK = config.ControllerGain;
        if (divergenceLevel == DivergenceLevel.Degraded)
            effectiveK *= config.DegradedGainFactor;

        double rawDelta = effectiveK * deviation;
        return Math.Clamp(rawDelta, -config.MaxStepPerAdjust, config.MaxStepPerAdjust);
    }

    /// <summary>
    /// 判断调整方向
    /// </summary>
    public static AdjustmentDirection GetDirection(double delta)
    {
        if (Math.Abs(delta) < 1e-10) return AdjustmentDirection.None;
        return delta > 0 ? AdjustmentDirection.Down : AdjustmentDirection.Up;
    }
}
