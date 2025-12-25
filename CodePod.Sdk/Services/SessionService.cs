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

        // 先预留一个容器：如果这里拿不到，就直接失败，不写入任何 session 记录。
        ContainerInfo? container = await _poolService.AcquireContainerAsync(resourceLimits, networkMode, cancellationToken);
        if (container == null)
        {
            throw new MaxContainersReachedException(_config.MaxContainers);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        int? createdSessionId = null;
        try
        {
            await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                SessionEntity sessionEntity = new()
                {
                    Name = options.Name,
                    CreatedAt = now,
                    LastActivityAt = now,
                    Status = SessionStatus.Active,
                    TimeoutSeconds = options.TimeoutSeconds,
                    ResourceLimitsJson = System.Text.Json.JsonSerializer.Serialize(resourceLimits),
                    NetworkMode = networkMode,
                    ContainerId = container.ContainerId,
                };

                context.Sessions.Add(sessionEntity);
                await context.SaveChangesAsync(cancellationToken);

                createdSessionId = sessionEntity.Id;

                if (string.IsNullOrEmpty(sessionEntity.Name))
                {
                    sessionEntity.Name = $"Session-{createdSessionId.Value}";
                    await context.SaveChangesAsync(cancellationToken);
                }
            }

            _logger?.LogInformation("Session {SessionId} created with container {ContainerId} (memory: {Memory}MB, cpu: {Cpu}, network: {Network})",
                createdSessionId.Value, container.ShortId, resourceLimits.MemoryBytes / 1024 / 1024, resourceLimits.CpuCores, networkMode);
        }
        catch
        {
            // 如果创建过程中任何一步失败，释放容器并尽量删除 session 记录（避免落库垃圾数据）
            try
            {
                await _poolService.ReleaseContainerAsync(container.ContainerId, cancellationToken);
            }
            catch
            {
                // ignore
            }

            if (createdSessionId.HasValue)
            {
                try
                {
                    await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                    SessionEntity? entity = await context.Sessions.FindAsync([createdSessionId.Value], cancellationToken);
                    if (entity != null)
                    {
                        context.Sessions.Remove(entity);
                        await context.SaveChangesAsync(cancellationToken);
                    }
                }
                catch
                {
                    // ignore
                }
            }
            throw;
        }

        // 返回最新状态（必须 ready）
        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            SessionEntity? entity = await context.Sessions.FindAsync([createdSessionId!.Value], cancellationToken);
            if (entity == null)
            {
                throw new SessionNotFoundException(createdSessionId!.Value);
            }

            return entity.ToModel();
        }
    }

    public async Task<IEnumerable<SessionInfo>> GetAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        List<SessionEntity> entities = await context.Sessions
            .Where(s => s.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);
        return entities.Select(e => e.ToModel()).ToList();
    }

    public async Task<SessionInfo?> GetSessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        SessionEntity? entity = await context.Sessions.FindAsync([sessionId], cancellationToken);
        if (entity == null || entity.Status != SessionStatus.Active)
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

    public async Task OnContainerDeletedAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        SessionEntity? entity = await context.Sessions.FirstOrDefaultAsync(s => s.ContainerId == containerId, cancellationToken);
        if (entity != null)
        {
            entity.Status = SessionStatus.Destroyed;
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

    // Queued 语义已废弃：不再有排队、提升、位置更新等逻辑。
}
