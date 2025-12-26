using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk.Services;

/// <summary>
/// Docker池服务接口
/// </summary>
public interface IDockerPoolService : IDisposable
{
    /// <summary>
    /// 确保预热容器
    /// </summary>
    Task EnsurePrewarmAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 同步 Docker 实际状态到数据库
    /// </summary>
    Task SyncStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取一个可用容器（先预留，不绑定 SessionId；用于避免创建 Session 时产生占位记录）
    /// </summary>
    Task<ContainerInfo?> AcquireContainerAsync(ResourceLimits resourceLimits, NetworkMode networkMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放容器（销毁）
    /// </summary>
    Task ReleaseContainerAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新容器（手动）
    /// </summary>
    Task<ContainerInfo> CreateContainerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 强制删除容器
    /// </summary>
    Task ForceDeleteContainerAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除所有容器
    /// </summary>
    Task DeleteAllContainersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有容器
    /// </summary>
    Task<List<ContainerInfo>> GetAllContainersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 状态变化事件
    /// </summary>
    event EventHandler? OnStatusChanged;
}

/// <summary>
/// Docker池服务实现
/// </summary>
public class DockerPoolService : IDockerPoolService, IDisposable
{
    private readonly IDockerService _dockerService;
    private readonly IDbContextFactory<CodePodDbContext> _contextFactory;
    private readonly CodePodConfig _config;
    private readonly ILogger<DockerPoolService>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _backgroundCts = new();
    private bool _disposed = false;

    public event EventHandler? OnStatusChanged;

    public DockerPoolService(
        IDockerService dockerService,
        IDbContextFactory<CodePodDbContext> contextFactory,
        CodePodConfig config,
        ILogger<DockerPoolService>? logger = null)
    {
        _dockerService = dockerService;
        _contextFactory = contextFactory;
        _config = config;
        _logger = logger;
    }

    public async Task EnsurePrewarmAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Prewarming Docker containers...");

        // 计算需要预热的容器数量
        (int idle, int busy, int warming, int _) = await GetCountByStatusAsync(cancellationToken);
        int currentUsable = idle + busy + warming;
        int needToWarm = Math.Max(0, _config.PrewarmCount - idle);
        int canWarm = Math.Max(0, _config.MaxContainers - currentUsable);
        int toWarm = Math.Min(needToWarm, canWarm);

        if (toWarm > 0)
        {
            _logger?.LogInformation("Starting to warm {Count} containers (current: {Idle} idle, {Busy} busy)...",
                toWarm, idle, busy);
            List<Task> prewarmTasks = new();
            for (int i = 0; i < toWarm; i++)
            {
                prewarmTasks.Add(CreateAndWarmContainerAsync(cancellationToken));
            }
            await Task.WhenAll(prewarmTasks);
        }
        else
        {
            _logger?.LogInformation("No need to warm containers (current: {Idle} idle, {Busy} busy)", idle, busy);
        }

        int containerCount = await GetContainerCountAsync(cancellationToken);
        _logger?.LogInformation("Docker prewarm completed, {Count} containers in database", containerCount);
        NotifyStatusChanged();
    }

    public async Task SyncStateAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Starting Docker state synchronization...");

            List<ContainerInfo> dockerContainers = await _dockerService.GetManagedContainersAsync(cancellationToken);
            HashSet<string> dockerContainerIds = dockerContainers.Select(c => c.ContainerId).ToHashSet();

            List<ContainerEntity> dbContainers;
            List<SessionEntity> dbSessions;
            await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                dbContainers = await context.Containers.ToListAsync(cancellationToken);
                dbSessions = await context.Sessions.Where(s => s.Status != SessionStatus.Destroyed).ToListAsync(cancellationToken);
            }

            HashSet<string> busyContainerIds = dbSessions
                .Where(s => s.Status == SessionStatus.Active && !string.IsNullOrEmpty(s.ContainerId))
                .Select(s => s.ContainerId!)
                .ToHashSet();

            HashSet<string> dbContainerIds = dbContainers.Select(c => c.ContainerId).ToHashSet();

            // 1. DB exists but Docker missing
            List<string> deletedContainerIds = dbContainerIds.Except(dockerContainerIds).ToList();
            foreach (string containerId in deletedContainerIds)
            {
                await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);

                SessionEntity? session = await context.Sessions.FirstOrDefaultAsync(s => s.ContainerId == containerId, cancellationToken);
                if (session != null && session.Status != SessionStatus.Destroyed)
                {
                    session.Status = SessionStatus.Destroyed;
                }

                ContainerEntity? container = await context.Containers.FindAsync([containerId], cancellationToken);
                if (container != null)
                {
                    context.Containers.Remove(container);
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            // 2. Docker exists but DB missing
            List<string> newContainerIds = dockerContainerIds.Except(dbContainerIds).ToList();
            foreach (string containerId in newContainerIds)
            {
                ContainerInfo dockerContainer = dockerContainers.First(c => c.ContainerId == containerId);
                bool isRunning = dockerContainer.DockerStatus == "running";

                if (isRunning)
                {
                    ContainerStatus status = busyContainerIds.Contains(containerId)
                        ? ContainerStatus.Busy
                        : ContainerStatus.Idle;

                    await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                    context.Containers.Add(ContainerEntity.FromModel(new ContainerInfo
                    {
                        ContainerId = dockerContainer.ContainerId,
                        Name = dockerContainer.Name,
                        Image = dockerContainer.Image,
                        DockerStatus = dockerContainer.DockerStatus,
                        Status = status,
                        CreatedAt = dockerContainer.CreatedAt,
                        StartedAt = dockerContainer.StartedAt,
                        Labels = dockerContainer.Labels
                    }));
                    await context.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    try
                    {
                        await _dockerService.DeleteContainerAsync(containerId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete non-running container {ContainerId}", dockerContainer.ShortId);
                    }
                }
            }

            // 3. Update existing
            List<string> existingContainerIds = dbContainerIds.Intersect(dockerContainerIds).ToList();
            foreach (string containerId in existingContainerIds)
            {
                ContainerInfo dockerContainer = dockerContainers.First(c => c.ContainerId == containerId);
                ContainerEntity dbContainer = dbContainers.First(c => c.ContainerId == containerId);
                bool isRunning = dockerContainer.DockerStatus == "running";

                if (!isRunning)
                {
                    await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);

                    SessionEntity? session = await context.Sessions.FirstOrDefaultAsync(s => s.ContainerId == containerId && s.Status != SessionStatus.Destroyed, cancellationToken);
                    if (session != null)
                    {
                        session.Status = SessionStatus.Destroyed;
                    }

                    ContainerEntity? container = await context.Containers.FindAsync([containerId], cancellationToken);
                    if (container != null)
                    {
                        context.Containers.Remove(container);
                    }
                    await context.SaveChangesAsync(cancellationToken);

                    try
                    {
                        await _dockerService.DeleteContainerAsync(containerId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete stopped container {ContainerId}", dockerContainer.ShortId);
                    }
                }
                else
                {
                    ContainerStatus expectedStatus = busyContainerIds.Contains(containerId)
                        ? ContainerStatus.Busy
                        : ContainerStatus.Idle;

                    if (dbContainer.Status != expectedStatus || dbContainer.Status == ContainerStatus.Warming || dbContainer.Status == ContainerStatus.Destroying)
                    {
                        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                        ContainerEntity? container = await context.Containers.FindAsync([containerId], cancellationToken);
                        if (container != null)
                        {
                            container.Status = expectedStatus;
                            await context.SaveChangesAsync(cancellationToken);
                        }
                    }
                }
            }

            // 4. Remove orphaned warming/destroying
            await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                List<ContainerEntity> orphanedContainers = await context.Containers
                    .Where(c => (c.Status == ContainerStatus.Destroying || c.Status == ContainerStatus.Warming) &&
                               !dockerContainerIds.Contains(c.ContainerId))
                    .ToListAsync(cancellationToken);

                foreach (ContainerEntity container in orphanedContainers)
                {
                    context.Containers.Remove(container);
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            // 5. Mark orphaned sessions
            await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                List<SessionEntity> orphanedSessions = await context.Sessions
                    .Where(s => s.Status != SessionStatus.Destroyed &&
                               s.ContainerId != null &&
                               !dockerContainerIds.Contains(s.ContainerId))
                    .ToListAsync(cancellationToken);

                foreach (SessionEntity session in orphanedSessions)
                {
                    session.Status = SessionStatus.Destroyed;
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            _logger?.LogInformation("Docker state synchronization completed");
            NotifyStatusChanged();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ContainerInfo?> AcquireContainerAsync(ResourceLimits resourceLimits, NetworkMode networkMode, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // 查找空闲容器（仅当使用默认配置时才能复用预热容器）
            bool isDefaultConfig = resourceLimits.MemoryBytes == _config.DefaultResourceLimits.MemoryBytes &&
                                   Math.Abs(resourceLimits.CpuCores - _config.DefaultResourceLimits.CpuCores) < 0.01 &&
                                   resourceLimits.MaxProcesses == _config.DefaultResourceLimits.MaxProcesses &&
                                   networkMode == _config.DefaultNetworkMode;

            if (isDefaultConfig)
            {
                ContainerInfo? idleContainer = await GetFirstIdleContainerAsync(cancellationToken);
                if (idleContainer != null)
                {
                    idleContainer.Status = ContainerStatus.Busy;
                    await SaveContainerAsync(idleContainer, cancellationToken);
                    _logger?.LogInformation("Reserved container {ContainerId} (unbound)", idleContainer.ShortId);
                    NotifyStatusChanged();

                    // 在后台补充预热容器（如果还有空间）- 使用内部 CancellationToken
                    _ = TryPrewarmOneAsync(_backgroundCts.Token);

                    return idleContainer;
                }
            }

            // 检查是否达到最大容器数（排除正在销毁的容器）
            (int idleCount, int busyCount, int warmingCount, int _) = await GetCountByStatusAsync(cancellationToken);
            int activeContainerCount = idleCount + busyCount + warmingCount;
            if (activeContainerCount >= _config.MaxContainers)
            {
                _logger?.LogWarning("Max container count {Max} reached (active: {Active}), cannot allocate", _config.MaxContainers, activeContainerCount);
                return null;
            }

            // 创建新容器（不提前绑定 SessionId，后续由 BindContainerToSessionAsync 绑定）
            ContainerInfo newContainer = await _dockerService.CreateContainerAsync(resourceLimits, networkMode, cancellationToken);
            newContainer.Status = ContainerStatus.Busy;
            await SaveContainerAsync(newContainer, cancellationToken);
            _logger?.LogInformation("Created and reserved new container {ContainerId} (unbound) (memory: {Memory}MB, cpu: {Cpu}, network: {Network})",
                newContainer.ShortId, resourceLimits.MemoryBytes / 1024 / 1024, resourceLimits.CpuCores, networkMode);
            NotifyStatusChanged();
            return newContainer;
        }
        finally
        {
            _lock.Release();
        }
    }


    private async Task TryPrewarmOneAsync(CancellationToken cancellationToken)
    {
        try
        {
            int neededCount;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                (int idleCount, int busyCount, int warmingCount, int _) = await GetCountByStatusAsync(cancellationToken);
                int activeCount = idleCount + busyCount + warmingCount;

                int currentAvailable = idleCount + warmingCount;
                int maxCanCreate = _config.MaxContainers - activeCount;
                neededCount = Math.Min(_config.PrewarmCount - currentAvailable, maxCanCreate);

                if (neededCount <= 0)
                {
                    return;
                }

                _logger?.LogInformation("Background prewarm: idle: {Idle}, warming: {Warming}, need to create: {Needed}",
                    idleCount, warmingCount, neededCount);
            }
            finally
            {
                _lock.Release();
            }

            List<Task> tasks = new();
            for (int i = 0; i < neededCount; i++)
            {
                tasks.Add(CreateAndWarmContainerAsync(cancellationToken));
            }
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Background prewarm failed");
        }
    }

    public async Task ReleaseContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                ContainerEntity? entity = await context.Containers.FindAsync([containerId], cancellationToken);
                if (entity != null)
                {
                    entity.Status = ContainerStatus.Destroying;
                    await context.SaveChangesAsync(cancellationToken);
                    NotifyStatusChanged();
                }
            }

            await DeleteContainerAsync(containerId, cancellationToken);
            await _dockerService.DeleteContainerAsync(containerId, cancellationToken);
            _logger?.LogInformation("Released and deleted container {ContainerId}", containerId[..Math.Min(12, containerId.Length)]);
            NotifyStatusChanged();
        }
        finally
        {
            _lock.Release();
        }

        // 在后台补充预热容器 - 使用内部 CancellationToken
        _ = TryPrewarmOneAsync(_backgroundCts.Token);
    }

    public async Task ForceDeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await ReleaseContainerAsync(containerId, cancellationToken);
    }

    public async Task<ContainerInfo> CreateContainerAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            (int idleCount, int busyCount, int warmingCount, int _) = await GetCountByStatusAsync(cancellationToken);
            int activeCount = idleCount + busyCount + warmingCount;
            if (activeCount >= _config.MaxContainers)
            {
                throw new InvalidOperationException($"Max container count {_config.MaxContainers} reached");
            }

            ContainerInfo container = await CreateAndWarmContainerAsync(cancellationToken);
            _logger?.LogInformation("Manually created container {ContainerId}", container.ShortId);
            return container;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAllContainersAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            List<ContainerEntity> containers;
            await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                containers = await context.Containers.ToListAsync(cancellationToken);

                // 标记所有容器为销毁中
                foreach (ContainerEntity container in containers)
                {
                    container.Status = ContainerStatus.Destroying;
                }
                await context.SaveChangesAsync(cancellationToken);
            }
            NotifyStatusChanged();

            // 并行删除所有容器
            IEnumerable<Task> deleteTasks = containers.Select(async c =>
            {
                try
                {
                    await _dockerService.DeleteContainerAsync(c.ContainerId, cancellationToken);
                    await DeleteContainerAsync(c.ContainerId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete container: {ContainerId}", c.ContainerId[..Math.Min(12, c.ContainerId.Length)]);
                }
            });
            await Task.WhenAll(deleteTasks);

            _logger?.LogInformation("All containers deleted");
            NotifyStatusChanged();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ContainerInfo>> GetAllContainersAsync(CancellationToken cancellationToken = default)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        List<ContainerEntity> entities = await context.Containers.ToListAsync(cancellationToken);
        return entities.Select(e => e.ToModel()).ToList();
    }

    private async Task<ContainerInfo> CreateAndWarmContainerAsync(CancellationToken cancellationToken)
    {
        // 创建占位容器信息（预热中状态）
        string tempId = Guid.NewGuid().ToString("N");
        ContainerInfo warmingContainer = new()
        {
            ContainerId = tempId,
            Name = "warming...",
            Image = _config.Image,
            DockerStatus = "creating",
            Status = ContainerStatus.Warming,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await SaveContainerAsync(warmingContainer, cancellationToken);
        NotifyStatusChanged();

        try
        {
            // 创建实际容器
            ContainerInfo containerInfo = await _dockerService.CreateContainerAsync(cancellationToken: cancellationToken);

            // 等待容器进入running状态
            TimeSpan maxWait = TimeSpan.FromSeconds(30);
            TimeSpan waited = TimeSpan.Zero;
            TimeSpan pollInterval = TimeSpan.FromMilliseconds(500);

            while (waited < maxWait)
            {
                ContainerInfo? inspected = await _dockerService.GetContainerAsync(containerInfo.ContainerId, cancellationToken);
                if (inspected != null && inspected.DockerStatus == "running")
                {
                    break;
                }
                await Task.Delay(pollInterval, cancellationToken);
                waited += pollInterval;
            }

            // 执行一个简单命令确保容器就绪
            await _dockerService.ExecuteCommandAsync(containerInfo.ContainerId, "echo ready", "/app", 30, cancellationToken);

            // 移除临时占位，添加真实容器
            await DeleteContainerAsync(tempId, cancellationToken);

            ContainerInfo readyContainer = new()
            {
                ContainerId = containerInfo.ContainerId,
                Name = containerInfo.Name,
                Image = containerInfo.Image,
                DockerStatus = "running",
                Status = ContainerStatus.Idle,
                CreatedAt = containerInfo.CreatedAt,
                StartedAt = DateTimeOffset.UtcNow,
                Labels = containerInfo.Labels
            };

            await SaveContainerAsync(readyContainer, cancellationToken);
            _logger?.LogInformation("Container {ContainerId} warmed up", readyContainer.ShortId);
            NotifyStatusChanged();

            return readyContainer;
        }
        catch
        {
            await DeleteContainerAsync(tempId, cancellationToken);
            NotifyStatusChanged();
            throw;
        }
    }

    #region Database Operations

    private async Task SaveContainerAsync(ContainerInfo container, CancellationToken cancellationToken)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        ContainerEntity? existing = await context.Containers.FindAsync([container.ContainerId], cancellationToken);
        if (existing == null)
        {
            context.Containers.Add(ContainerEntity.FromModel(container));
        }
        else
        {
            existing.UpdateFromModel(container);
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task DeleteContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        ContainerEntity? entity = await context.Containers.FindAsync([containerId], cancellationToken);
        if (entity != null)
        {
            context.Containers.Remove(entity);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<ContainerInfo?> GetFirstIdleContainerAsync(CancellationToken cancellationToken)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        ContainerEntity? entity = await context.Containers
            .Where(c => c.Status == ContainerStatus.Idle)
            .FirstOrDefaultAsync(cancellationToken);
        return entity?.ToModel();
    }

    private async Task<(int idle, int busy, int warming, int destroying)> GetCountByStatusAsync(CancellationToken cancellationToken)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var counts = await context.Containers
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return (
            idle: counts.FirstOrDefault(c => c.Status == ContainerStatus.Idle)?.Count ?? 0,
            busy: counts.FirstOrDefault(c => c.Status == ContainerStatus.Busy)?.Count ?? 0,
            warming: counts.FirstOrDefault(c => c.Status == ContainerStatus.Warming)?.Count ?? 0,
            destroying: counts.FirstOrDefault(c => c.Status == ContainerStatus.Destroying)?.Count ?? 0
        );
    }

    private async Task<int> GetContainerCountAsync(CancellationToken cancellationToken)
    {
        await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Containers.CountAsync(cancellationToken);
    }

    #endregion

    private void NotifyStatusChanged()
    {
        try
        {
            OnStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Status change notification failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 取消所有后台任务
        _backgroundCts.Cancel();
        _backgroundCts.Dispose();
        _lock.Dispose();

        GC.SuppressFinalize(this);
    }
}
