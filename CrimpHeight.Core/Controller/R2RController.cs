using CrimpHeight.Core.Models;

namespace CrimpHeight.Core.Controller;

/// <summary>
/// R2R 控制器 — PRD §B 控制律
/// K-only (DeadbandDamped) 模式，β-无关，带自适应增 K
/// 可独立复用
/// </summary>
public class R2RController
{
    // === 自适应增 K 状态 ===
    // 检测收敛停滞：连续多次偏差方向一致、幅度变化小但进不了死区，逐步增 K。
    // 偏差进入死区或方向反转时恢复/回退 K。
    // 所有阈值参数从 ControllerConfig 读取，不硬编码。

    private int _stallCount;                // 连续停滞次数
    private double _lastDeviation = double.NaN; // 上次偏差值

    /// <summary>当前生效的 K 增量（0 = 未增 K）</summary>
    public double CurrentKBoost { get; private set; }

    /// <summary>
    /// 计算调整量 — 核心控制律
    /// Δ = K × Deviation，带死区、步长限幅和自适应增 K
    /// </summary>
    public double ComputeAdjustment(
        double trimmedMean,
        ControllerConfig config,
        DivergenceLevel divergenceLevel)
    {
        config.Validate();

        double target = config.TargetMidpoint;
        double deviation = trimmedMean - target;

        // 死区判断：偏差在容差范围内，不动作，恢复 K
        if (deviation >= -config.LowerTolerance
            && deviation <= config.UpperTolerance)
        {
            ResetKBoost();
            return 0;
        }

        // 自适应增 K：检测收敛停滞
        UpdateStallDetection(deviation, config);

        // 确定有效 K = 基础 K + 自适应增量，上限由配置决定
        double effectiveK = Math.Min(config.ControllerGain + CurrentKBoost, config.AdaptiveKMaxLimit);
        if (divergenceLevel == DivergenceLevel.Degraded)
        {
            effectiveK *= config.DegradedGainFactor;
        }

        double rawDelta = effectiveK * deviation;
        double clampedDelta = Math.Clamp(rawDelta, -config.MaxStepPerAdjust, config.MaxStepPerAdjust);

        return clampedDelta;
    }

    /// <summary>
    /// 计算调整量 — 绕过死区版本，用于漂移确认触发。
    /// 漂移确认意味着趋势已确认在往外跑，即使偏差还在死区内也应提前补偿。
    /// </summary>
    public double ComputeAdjustmentBypassDeadband(
        double trimmedMean,
        ControllerConfig config,
        DivergenceLevel divergenceLevel)
    {
        config.Validate();

        double target = config.TargetMidpoint;
        double deviation = trimmedMean - target;

        if (Math.Abs(deviation) < 1e-10)
            return 0;

        double effectiveK = Math.Min(config.ControllerGain + CurrentKBoost, config.AdaptiveKMaxLimit);
        if (divergenceLevel == DivergenceLevel.Degraded)
            effectiveK *= config.DegradedGainFactor;

        double rawDelta = effectiveK * deviation;
        return Math.Clamp(rawDelta, -config.MaxStepPerAdjust, config.MaxStepPerAdjust);
    }

    /// <summary>
    /// 检测收敛停滞并更新 K 增量。
    /// 停滞条件：偏差方向与上次一致，且幅度变化率低于配置阈值（说明调整没怎么起效）。
    /// 所有阈值从 ControllerConfig 读取。
    /// </summary>
    private void UpdateStallDetection(double deviation, ControllerConfig config)
    {
        if (double.IsNaN(_lastDeviation))
        {
            _lastDeviation = deviation;
            return;
        }

        bool sameDirection = Math.Sign(deviation) == Math.Sign(_lastDeviation);
        double changeRatio = Math.Abs(_lastDeviation) > 1e-10
            ? Math.Abs(Math.Abs(deviation) - Math.Abs(_lastDeviation)) / Math.Abs(_lastDeviation)
            : 1.0;

        if (sameDirection && changeRatio < config.AdaptiveKStallChangeRatio)
        {
            // 收敛停滞：方向一致，幅度几乎没变
            _stallCount++;
            if (_stallCount >= config.AdaptiveKStallThreshold)
            {
                double maxBoost = config.AdaptiveKMaxLimit - config.ControllerGain;
                CurrentKBoost = Math.Min(CurrentKBoost + config.AdaptiveKBoostStep, maxBoost);
            }
        }
        else if (!sameDirection)
        {
            // 方向反转 → 过调了，当前 K 太大，回退一档
            _stallCount = 0;
            CurrentKBoost = Math.Max(0, CurrentKBoost - config.AdaptiveKBoostStep);
        }
        else
        {
            // 方向一致但幅度变化明显 → 调整在起效，保持当前 K，重置停滞计数
            _stallCount = 0;
        }

        _lastDeviation = deviation;
    }

    /// <summary>
    /// 恢复原始 K（偏差进入死区、方向反转、或外部重置时调用）
    /// </summary>
    public void ResetKBoost()
    {
        _stallCount = 0;
        CurrentKBoost = 0;
        _lastDeviation = double.NaN;
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
