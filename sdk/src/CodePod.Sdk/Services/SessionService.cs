using CodePod.Sdk.Configuration;
using CodePod.Sdk.Exceptions;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk.Services;

/// <summary>
/// 会话服务接口
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// 创建会话
    /// </summary>
    Task<SessionInfo> CreateSessionAsync(string? name = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建会话（带完整选项）
    /// </summary>
    Task<SessionInfo> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取系统配置的最大超时时间（秒）
    /// </summary>
    int MaxTimeoutSeconds { get; }

    /// <summary>
    /// 获取所有会话
    /// </summary>
    Task<IEnumerable<SessionInfo>> GetAllSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取会话
    /// </summary>
    Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 销毁会话
    /// </summary>
    Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新会话活动时间
    /// </summary>
    Task UpdateSessionActivityAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取队列中等待的会话数
    /// </summary>
    Task<int> GetQueuedCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 处理容器被删除的事件
    /// </summary>
    Task OnContainerDeletedAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 递增命令计数
    /// </summary>
    Task IncrementCommandCountAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置会话的命令执行状态
    /// </summary>
    Task SetExecutingCommandAsync(string sessionId, bool isExecuting, CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话服务实现
/// </summary>
public class SessionService : ISessionService
{
    private readonly ISessionStorage _sessionStorage;
    private readonly IDockerPoolService _poolService;
    private readonly ILogger<SessionService>? _logger;
    private readonly CodePodConfig _config;
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    public SessionService(
        ISessionStorage sessionStorage,
        IDockerPoolService poolService,
        CodePodConfig config,
        ILogger<SessionService>? logger = null)
    {
        _sessionStorage = sessionStorage;
        _poolService = poolService;
        _config = config;
        _logger = logger;
    }

    public int MaxTimeoutSeconds => _config.SessionTimeoutSeconds;

    public Task<SessionInfo> CreateSessionAsync(string? name = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
    {
        return CreateSessionAsync(new SessionOptions
        {
            Name = name,
            TimeoutSeconds = timeoutSeconds
        }, cancellationToken);
    }

    public async Task<SessionInfo> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken = default)
    {
        // 验证超时时间
        if (options.TimeoutSeconds.HasValue && options.TimeoutSeconds.Value > _config.SessionTimeoutSeconds)
        {
            throw new TimeoutExceedsLimitException(options.TimeoutSeconds.Value, _config.SessionTimeoutSeconds);
        }

        // 验证资源限制
        var resourceLimits = options.ResourceLimits ?? _config.DefaultResourceLimits.Clone();
        resourceLimits.Validate(_config.MaxResourceLimits);

        // 网络模式
        var networkMode = options.NetworkMode ?? _config.DefaultNetworkMode;

        var sessionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var session = new SessionInfo
        {
            SessionId = sessionId,
            Name = options.Name ?? $"Session-{sessionId[..8]}",
            CreatedAt = now,
            LastActivityAt = now,
            Status = SessionStatus.Queued,
            TimeoutSeconds = options.TimeoutSeconds,
            ResourceLimits = resourceLimits,
            NetworkMode = networkMode
        };

        await _sessionStorage.SaveAsync(session, cancellationToken);
        _logger?.LogInformation("Session {SessionId} created (memory: {Memory}MB, cpu: {Cpu}, network: {Network})",
            sessionId, resourceLimits.MemoryBytes / 1024 / 1024, resourceLimits.CpuCores, networkMode);

        // 尝试分配容器（带资源限制和网络模式）
        var container = await _poolService.AcquireContainerAsync(sessionId, resourceLimits, networkMode, cancellationToken);

        if (container != null)
        {
            session.ContainerId = container.ContainerId;
            session.Status = SessionStatus.Active;
            await _sessionStorage.SaveAsync(session, cancellationToken);
            _logger?.LogInformation("Session {SessionId} acquired container {ContainerId}", sessionId, container.ShortId);
        }
        else
        {
            // 加入队列
            var queuedCount = await GetQueuedCountAsync(cancellationToken);
            session.QueuePosition = queuedCount;
            session.Status = SessionStatus.Queued;
            await _sessionStorage.SaveAsync(session, cancellationToken);
            _logger?.LogInformation("Session {SessionId} queued at position {Position}", sessionId, session.QueuePosition);
        }

        return session;
    }

    public async Task<IEnumerable<SessionInfo>> GetAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await _sessionStorage.GetAllActiveAsync(cancellationToken);
    }

    public async Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionStorage.GetAsync(sessionId, cancellationToken);
        if (session == null || session.Status == SessionStatus.Destroyed)
        {
            return null;
        }
        return session;
    }

    public async Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionStorage.GetAsync(sessionId, cancellationToken);
        if (session == null || session.Status == SessionStatus.Destroyed)
        {
            return;
        }

        session.Status = SessionStatus.Destroyed;
        await _sessionStorage.SaveAsync(session, cancellationToken);
        _logger?.LogInformation("Session {SessionId} destroyed", sessionId);

        // 释放并删除容器
        if (!string.IsNullOrEmpty(session.ContainerId))
        {
            await _poolService.ReleaseContainerAsync(session.ContainerId, cancellationToken);
        }

        // 尝试处理队列
        if (await GetQueuedCountAsync(cancellationToken) > 0)
        {
            await TryPromoteQueueWithRetryAsync(cancellationToken);
        }
    }

    public async Task UpdateSessionActivityAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionStorage.GetAsync(sessionId, cancellationToken);
        if (session != null)
        {
            session.LastActivityAt = DateTimeOffset.UtcNow;
            await _sessionStorage.SaveAsync(session, cancellationToken);
        }
    }

    public async Task<int> GetQueuedCountAsync(CancellationToken cancellationToken = default)
    {
        var queued = await _sessionStorage.GetQueuedSessionsAsync(cancellationToken);
        return queued.Count;
    }

    public async Task OnContainerDeletedAsync(string containerId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionStorage.GetByContainerIdAsync(containerId, cancellationToken);
        if (session != null)
        {
            session.Status = SessionStatus.Destroyed;
            session.ContainerId = null;
            await _sessionStorage.SaveAsync(session, cancellationToken);
            _logger?.LogInformation("Container {ContainerId} deleted, session {SessionId} marked as destroyed", containerId[..12], session.SessionId);
        }
    }

    public async Task IncrementCommandCountAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionStorage.GetAsync(sessionId, cancellationToken);
        if (session != null)
        {
            session.CommandCount++;
            session.LastActivityAt = DateTimeOffset.UtcNow;
            await _sessionStorage.SaveAsync(session, cancellationToken);
        }
    }

    public async Task SetExecutingCommandAsync(string sessionId, bool isExecuting, CancellationToken cancellationToken = default)
    {
        var session = await _sessionStorage.GetAsync(sessionId, cancellationToken);
        if (session != null)
        {
            session.IsExecutingCommand = isExecuting;
            if (isExecuting)
            {
                session.LastActivityAt = DateTimeOffset.UtcNow;
            }
            await _sessionStorage.SaveAsync(session, cancellationToken);
        }
    }

    private async Task TryPromoteQueueWithRetryAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 10;
        const int retryDelayMs = 500;

        for (int i = 0; i < maxRetries; i++)
        {
            if (await TryPromoteQueueOnceAsync(cancellationToken))
            {
                return;
            }

            if (await GetQueuedCountAsync(cancellationToken) == 0)
            {
                return;
            }

            await Task.Delay(retryDelayMs, cancellationToken);
        }

        _logger?.LogWarning("Failed to promote queued sessions after {MaxRetries} retries", maxRetries);
    }

    private async Task<bool> TryPromoteQueueOnceAsync(CancellationToken cancellationToken)
    {
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            bool promoted = false;
            var queuedSessions = await _sessionStorage.GetQueuedSessionsAsync(cancellationToken);

            foreach (var session in queuedSessions.ToList())
            {
                var currentSession = await _sessionStorage.GetAsync(session.SessionId, cancellationToken);
                if (currentSession == null || currentSession.Status != SessionStatus.Queued)
                {
                    continue;
                }

                var container = await _poolService.AcquireContainerAsync(session.SessionId, cancellationToken);
                if (container != null)
                {
                    currentSession.ContainerId = container.ContainerId;
                    currentSession.Status = SessionStatus.Active;
                    currentSession.QueuePosition = 0;
                    await _sessionStorage.SaveAsync(currentSession, cancellationToken);
                    _logger?.LogInformation("Queued session {SessionId} acquired container {ContainerId}", session.SessionId, container.ShortId);
                    promoted = true;
                }
                else
                {
                    break;
                }
            }

            // 更新队列位置
            await UpdateQueuePositionsAsync(cancellationToken);

            return promoted;
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private async Task UpdateQueuePositionsAsync(CancellationToken cancellationToken)
    {
        var queuedSessions = await _sessionStorage.GetQueuedSessionsAsync(cancellationToken);
        var position = 1;
        foreach (var session in queuedSessions)
        {
            session.QueuePosition = position++;
            await _sessionStorage.SaveAsync(session, cancellationToken);
        }
    }
}
