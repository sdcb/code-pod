using System.Collections.Concurrent;
using DockerShellHost.Configuration;
using DockerShellHost.Models;
using Microsoft.Extensions.Options;

namespace DockerShellHost.Services;

/// <summary>
/// 会话服务接口
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// 创建会话
    /// </summary>
    /// <param name="name">会话名称</param>
    /// <param name="timeoutSeconds">超时时间（秒），null 使用系统默认值</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<SessionInfo> CreateSessionAsync(string? name = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取系统配置的最大超时时间（秒）
    /// </summary>
    int MaxTimeoutSeconds { get; }

    /// <summary>
    /// 获取所有会话
    /// </summary>
    IEnumerable<SessionInfo> GetAllSessions();

    /// <summary>
    /// 获取会话
    /// </summary>
    SessionInfo? GetSession(string sessionId);

    /// <summary>
    /// 销毁会话
    /// </summary>
    Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新会话活动时间
    /// </summary>
    void UpdateSessionActivity(string sessionId);

    /// <summary>
    /// 获取队列中等待的会话数
    /// </summary>
    int GetQueuedCount();

    /// <summary>
    /// 处理容器被删除的事件
    /// </summary>
    void OnContainerDeleted(string containerId);

    /// <summary>
    /// 递增命令计数
    /// </summary>
    void IncrementCommandCount(string sessionId);

    /// <summary>
    /// 设置会话的命令执行状态
    /// </summary>
    void SetExecutingCommand(string sessionId, bool isExecuting);
}

/// <summary>
/// 会话服务实现
/// </summary>
public class SessionService : ISessionService
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly IDockerPoolService _poolService;
    private readonly ILogger<SessionService> _logger;
    private readonly DockerPoolConfig _config;

    public SessionService(
        IDockerPoolService poolService,
        IOptions<DockerPoolConfig> config,
        ILogger<SessionService> logger)
    {
        _poolService = poolService;
        _config = config.Value;
        _logger = logger;
    }

    public int MaxTimeoutSeconds => _config.SessionTimeoutSeconds;

    public async Task<SessionInfo> CreateSessionAsync(string? name = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var session = new SessionInfo
        {
            SessionId = sessionId,
            Name = name ?? $"Session-{sessionId[..8]}",
            CreatedAt = now,
            LastActivityAt = now,
            Status = SessionStatus.Queued,
            TimeoutSeconds = timeoutSeconds
        };

        _sessions[sessionId] = session;
        _logger.LogInformation("Session {SessionId} created", sessionId);

        // 尝试分配容器
        var container = await _poolService.AcquireContainerAsync(sessionId, cancellationToken);

        if (container != null)
        {
            session.ContainerId = container.ContainerId;
            session.Status = SessionStatus.Active;
            _logger.LogInformation("Session {SessionId} acquired container {ContainerId}", sessionId, container.ShortId);
        }
        else
        {
            // 加入队列
            _queue.Enqueue(sessionId);
            session.QueuePosition = _queue.Count;
            session.Status = SessionStatus.Queued;
            _logger.LogInformation("Session {SessionId} queued at position {Position}", sessionId, session.QueuePosition);
        }

        return session;
    }

    public IEnumerable<SessionInfo> GetAllSessions()
    {
        return _sessions.Values.Where(s => s.Status != SessionStatus.Destroyed).ToList();
    }

    public SessionInfo? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) && session.Status != SessionStatus.Destroyed
            ? session
            : null;
    }

    public async Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        if (session.Status == SessionStatus.Destroyed)
        {
            return;
        }

        session.Status = SessionStatus.Destroyed;
        _logger.LogInformation("Session {SessionId} destroyed", sessionId);

        // 先释放并删除容器（容器是一次性的，不能重用）
        // ReleaseContainerAsync 会触发后台预热
        if (!string.IsNullOrEmpty(session.ContainerId))
        {
            await _poolService.ReleaseContainerAsync(session.ContainerId, cancellationToken);
        }

        // 然后尝试处理队列（在释放容器之后）
        // 等待一小段时间让预热容器完成（最多等待 5 秒）
        if (GetQueuedCount() > 0)
        {
            await TryPromoteQueueWithRetryAsync(cancellationToken);
        }
    }

    public void UpdateSessionActivity(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    public int GetQueuedCount()
    {
        return _sessions.Values.Count(s => s.Status == SessionStatus.Queued);
    }

    public void OnContainerDeleted(string containerId)
    {
        var session = _sessions.Values.FirstOrDefault(s => s.ContainerId == containerId);
        if (session != null)
        {
            session.Status = SessionStatus.Destroyed;
            session.ContainerId = null;
            _logger.LogInformation("Container {ContainerId} deleted, session {SessionId} marked as destroyed", containerId[..12], session.SessionId);
        }
    }

    public void IncrementCommandCount(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.CommandCount++;
            session.LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    public void SetExecutingCommand(string sessionId, bool isExecuting)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.IsExecutingCommand = isExecuting;
            if (isExecuting)
            {
                // 开始执行时更新活动时间
                session.LastActivityAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// 带重试的队列处理，等待预热容器完成
    /// </summary>
    private async Task TryPromoteQueueWithRetryAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 10;
        const int retryDelayMs = 500;

        for (int i = 0; i < maxRetries; i++)
        {
            if (await TryPromoteQueueOnceAsync(cancellationToken))
            {
                return; // 成功分配了容器
            }

            // 检查是否还有队列中的会话
            if (GetQueuedCount() == 0)
            {
                return;
            }

            // 等待一段时间让预热容器完成
            await Task.Delay(retryDelayMs, cancellationToken);
        }

        _logger.LogWarning("Failed to promote queued sessions after {MaxRetries} retries", maxRetries);
    }

    /// <summary>
    /// 尝试一次队列处理
    /// </summary>
    private async Task<bool> TryPromoteQueueOnceAsync(CancellationToken cancellationToken)
    {
        bool promoted = false;

        while (_queue.TryDequeue(out var sessionId))
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.Status != SessionStatus.Queued)
            {
                continue; // 跳过已销毁或已激活的会话
            }

            var container = await _poolService.AcquireContainerAsync(sessionId, cancellationToken);
            if (container != null)
            {
                session.ContainerId = container.ContainerId;
                session.Status = SessionStatus.Active;
                session.QueuePosition = 0;
                _logger.LogInformation("Queued session {SessionId} acquired container {ContainerId}", sessionId, container.ShortId);
                promoted = true;
            }
            else
            {
                // 没有可用容器，重新入队
                _queue.Enqueue(sessionId);
                break;
            }
        }

        // 更新队列位置
        UpdateQueuePositions();

        return promoted;
    }

    /// <summary>
    /// 更新队列位置
    /// </summary>
    private void UpdateQueuePositions()
    {
        var position = 1;
        foreach (var queuedSession in _sessions.Values.Where(s => s.Status == SessionStatus.Queued))
        {
            queuedSession.QueuePosition = position++;
        }
    }
}
