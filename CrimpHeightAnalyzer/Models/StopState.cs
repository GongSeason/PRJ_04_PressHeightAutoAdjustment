namespace CrimpHeightAnalyzer.Models;

/// <summary>
/// 停机水位线状态 — 持久化"已确认停机"进度，解决重启后重复读到同一批历史不良
/// 数据导致再次停机的问题。
///
/// 语义：只覆盖"数据内容触发"的停机（安全护栏 Outlier / 发散 Stop），
/// 不含数据失活（无数据本就不存在回放问题）。按压头分别记录。
/// </summary>
public class StopState
{
    /// <summary>压头 A（铆压#1）的停机水位线。</summary>
    public HeadStopState HeadA { get; set; } = new();

    /// <summary>压头 B（铆压#2）的停机水位线。</summary>
    public HeadStopState HeadB { get; set; } = new();
}

/// <summary>
/// 单个压头的停机水位线记录。
/// </summary>
public class HeadStopState
{
    /// <summary>
    /// 触发停机的那批数据的最新记录时间戳（水位线）。
    /// 为 null 表示该压头当前没有未恢复的内容触发停机。
    /// </summary>
    public DateTime? LastStopDataTimestamp { get; set; }

    /// <summary>停机原因（仲裁命中说明），用于审计与 UI 展示。</summary>
    public string? Reason { get; set; }

    /// <summary>水位线写入时的墙钟时刻，用于审计。</summary>
    public DateTime? RecordedAt { get; set; }
}
