using CodePod.Sdk.Models;

namespace CodePod.Sdk.Storage;

/// <summary>
/// 容器存储接口
/// </summary>
public interface IContainerStorage
{
    /// <summary>
    /// 保存容器信息
    /// </summary>
    Task SaveAsync(ContainerInfo container, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取容器信息
    /// </summary>
    Task<ContainerInfo?> GetAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有容器
    /// </summary>
    Task<IReadOnlyList<ContainerInfo>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除容器
    /// </summary>
    Task DeleteAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取空闲容器
    /// </summary>
    Task<ContainerInfo?> GetFirstIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取按状态分类的容器数量
    /// </summary>
    Task<(int idle, int busy, int warming, int destroying)> GetCountByStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取容器总数
    /// </summary>
    Task<int> GetCountAsync(CancellationToken cancellationToken = default);
}
