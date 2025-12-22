using CodePod.Sdk.Models;

namespace CodePod.Sdk.Storage;

/// <summary>
/// 会话存储接口
/// </summary>
public interface ISessionStorage
{
    /// <summary>
    /// 保存会话信息
    /// </summary>
    Task SaveAsync(SessionInfo session, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取会话信息
    /// </summary>
    Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有活动会话（未销毁）
    /// </summary>
    Task<IReadOnlyList<SessionInfo>> GetAllActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有会话
    /// </summary>
    Task<IReadOnlyList<SessionInfo>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除会话
    /// </summary>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据容器ID查找会话
    /// </summary>
    Task<SessionInfo?> GetByContainerIdAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取等待队列中的会话
    /// </summary>
    Task<IReadOnlyList<SessionInfo>> GetQueuedSessionsAsync(CancellationToken cancellationToken = default);
}
