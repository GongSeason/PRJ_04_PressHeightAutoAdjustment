using CrimpHeight.Core.Models;

namespace CrimpHeight.Core.Interfaces;

/// <summary>
/// 数据源抽象 — 解耦具体文件格式和存储位置
/// </summary>
public interface IDataProvider
{
    /// <summary>
    /// 获取最新的压接数据
    /// </summary>
    /// <param name="count">需要的数据条数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>按时间正序排列的压接数据</returns>
    Task<List<CrimpData>> GetLatestDataAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// 最后一次数据获取的时间戳
    /// </summary>
    DateTime? LastDataTimestamp { get; }
}
