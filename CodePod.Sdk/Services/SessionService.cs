using CodePod.Sdk.Configuration;
using CodePod.Sdk.Exceptions;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;
using Microsoft.EntityFrameworkCore;
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
    Task<SessionInfo?> GetSessionAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 销毁会话
    /// </summary>
    Task DestroySessionAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新会话活动时间
    /// </summary>
    Task UpdateSessionActivityAsync(int sessionId, CancellationToken cancellationToken = default);

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
    Task IncrementCommandCountAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置会话的命令执行状态
    /// </summary>
    Task SetExecutingCommandAsync(int sessionId, bool isExecuting, CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话服务实现
/// </summary>
public class SessionService : ISessionService
{
    private readonly IDbContextFactory<CodePodDbContext> _contextFactory;
    private readonly IDockerPoolService _poolService;
    private readonly ILogger<SessionService>? _logger;
    private readonly CodePodConfig _config;
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    public SessionService(
        IDbContextFactory<CodePodDbContext> contextFactory,
        IDockerPoolService poolService,
        CodePodConfig config,
        ILogger<SessionService>? logger = null)
    {
        _contextFactory = contextFactory;
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
        ResourceLimits resourceLimits = options.ResourceLimits ?? _config.DefaultResourceLimits.Clone();
        resourceLimits.Validate(_config.MaxResourceLimits);

        // 网络模式
        NetworkMode networkMode = options.NetworkMode ?? _config.DefaultNetworkMode;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        SessionEntity sessionEntity = new()
        {
            Name = options.Name,
            CreatedAt = now,
            LastActivityAt = now,
            Status = SessionStatus.Queued,
            TimeoutSeconds = options.TimeoutSeconds,
            ResourceLimitsJson = System.Text.Json.JsonSerializer.Serialize(resourceLimits),
            NetworkMode = networkMode
        };

        // 先保存以获取自增 ID
        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.Sessions.Add(sessionEntity);
            await context.SaveChangesAsync(cancellationToken);
        }

        var sessionId = sessionEntity.Id;

        // 更新 Name（如果未指定则使用 ID）
        if (string.IsNullOrEmpty(sessionEntity.Name))
        {
            await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
            if (entity != null)
            {
                entity.Name = $"Session-{sessionId}";
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        _logger?.LogInformation("Session {SessionId} created (memory: {Memory}MB, cpu: {Cpu}, network: {Network})",
            sessionId, resourceLimits.MemoryBytes / 1024 / 1024, resourceLimits.CpuCores, networkMode);

        // 尝试分配容器（带资源限制和网络模式）
        ContainerInfo? container = await _poolService.AcquireContainerAsync(sessionId, resourceLimits, networkMode, cancellationToken);

        if (container != null)
        {
            await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
            if (entity != null)
            {
                entity.ContainerId = container.ContainerId;
                entity.Status = SessionStatus.Active;
                await context.SaveChangesAsync(cancellationToken);
            }
            _logger?.LogInformation("Session {SessionId} acquired container {ContainerId}", sessionId, container.ShortId);
        }
        else
        {
            // 加入队列
            var queuedCount = await GetQueuedCountAsync(cancellationToken);
            await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
            if (entity != null)
            {
                entity.QueuePosition = queuedCount;
                entity.Status = SessionStatus.Queued;
                await context.SaveChangesAsync(cancellationToken);
            }
            _logger?.LogInformation("Session {SessionId} queued at position {Position}", sessionId, queuedCount);
        }

        // 返回最新状态
        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
            return entity!.ToModel();
        }
    }

    public async Task<IEnumerable<SessionInfo>> GetAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        List<SessionEntity> entities = await context.Sessions
            .Where(s => s.Status != SessionStatus.Destroyed)
            .ToListAsync(cancellationToken);
        return entities.Select(e => e.ToModel()).ToList();
    }

    public async Task<SessionInfo?> GetSessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
        if (entity == null || entity.Status == SessionStatus.Destroyed)
        {
            return null;
        }
        return entity.ToModel();
    }

    public async Task DestroySessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        string? containerId = null;

        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
            if (entity == null || entity.Status == SessionStatus.Destroyed)
            {
                return;
            }

            containerId = entity.ContainerId;
            entity.Status = SessionStatus.Destroyed;
            await context.SaveChangesAsync(cancellationToken);
        }

        _logger?.LogInformation("Session {SessionId} destroyed", sessionId);

        // 释放并删除容器
        if (!string.IsNullOrEmpty(containerId))
        {
            await _poolService.ReleaseContainerAsync(containerId, cancellationToken);
        }

        // 尝试处理队列
        if (await GetQueuedCountAsync(cancellationToken) > 0)
        {
            await TryPromoteQueueWithRetryAsync(cancellationToken);
        }
    }

    public async Task UpdateSessionActivityAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
        if (entity != null)
        {
            entity.LastActivityAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> GetQueuedCountAsync(CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Sessions.CountAsync(s => s.Status == SessionStatus.Queued, cancellationToken);
    }

    public async Task OnContainerDeletedAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        SessionEntity? entity = await context.Sessions.FirstOrDefaultAsync(s => s.ContainerId == containerId, cancellationToken);
        if (entity != null)
        {
            entity.Status = SessionStatus.Destroyed;
            entity.ContainerId = null;
            await context.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Container {ContainerId} deleted, session {SessionId} marked as destroyed", containerId[..12], entity.Id);
        }
    }

    public async Task IncrementCommandCountAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
        if (entity != null)
        {
            entity.CommandCount++;
            entity.LastActivityAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetExecutingCommandAsync(int sessionId, bool isExecuting, CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
        if (entity != null)
        {
            entity.IsExecutingCommand = isExecuting;
            if (isExecuting)
            {
                entity.LastActivityAt = DateTimeOffset.UtcNow;
            }
            await context.SaveChangesAsync(cancellationToken);
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

            // 获取队列中的会话
            List<SessionEntity> queuedEntities;
            await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                queuedEntities = await context.Sessions
                    .Where(s => s.Status == SessionStatus.Queued)
                    .OrderBy(s => s.QueuePosition)
                    .ToListAsync(cancellationToken);
            }

            foreach (SessionEntity queuedEntity in queuedEntities)
            {
                // 重新获取最新状态
                await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                SessionEntity? entity = await context.Sessions.FindAsync([queuedEntity.Id], cancellationToken);
                if (entity == null || entity.Status != SessionStatus.Queued)
                {
                    continue;
                }

                ContainerInfo? container = await _poolService.AcquireContainerAsync(entity.Id, cancellationToken);
                if (container != null)
                {
                    entity.ContainerId = container.ContainerId;
                    entity.Status = SessionStatus.Active;
                    entity.QueuePosition = 0;
                    await context.SaveChangesAsync(cancellationToken);
                    _logger?.LogInformation("Queued session {SessionId} acquired container {ContainerId}", entity.Id, container.ShortId);
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
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        List<SessionEntity> queuedEntities = await context.Sessions
            .Where(s => s.Status == SessionStatus.Queued)
            .OrderBy(s => s.QueuePosition)
            .ToListAsync(cancellationToken);

        var position = 1;
        foreach (SessionEntity? entity in queuedEntities)
        {
            entity.QueuePosition = position++;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
