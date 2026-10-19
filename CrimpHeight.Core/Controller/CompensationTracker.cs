namespace CrimpHeight.Core.Controller;

/// <summary>
/// 累计补偿量追踪器 — 每个压头一个实例。
/// 手动和自动调整共用同一个 tracker，PLC 永远收到 Accumulated（绝对补偿值）。
/// 不做软件侧限幅，由 PLC 侧卡限制。
/// </summary>
public class CompensationTracker
{
    /// <summary>当前累计补偿 (mm)，正=压头下降，负=压头上升</summary>
    public double Accumulated { get; private set; }

    /// <summary>
    /// 叠加增量并更新累计值。
    /// </summary>
    /// <param name="delta">本次增量 (mm)</param>
    /// <returns>实际叠加的增量</returns>
    public double Apply(double delta)
    {
        if (!double.IsFinite(delta))
            return 0;

        Accumulated += delta;
        return delta;
    }

    /// <summary>
    /// 重置累计值（用于写入失败回滚或外部恢复）。
    /// </summary>
    public void Reset(double value = 0) => Accumulated = value;
}
