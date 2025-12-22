using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk.Services;

/// <summary>
/// Docker池服务接口
/// </summary>
public interface IDockerPoolService
{
    /// <summary>
    /// 确保预热容器
    /// </summary>
    Task EnsurePrewarmAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取一个可用容器分配给会话
    /// </summary>
    Task<ContainerInfo?> AcquireContainerAsync(string sessionId, CancellationToken cancellationToken = default);

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
public class DockerPoolService : IDockerPoolService
{
    private readonly IDockerService _dockerService;
    private readonly IContainerStorage _containerStorage;
    private readonly CodePodConfig _config;
    private readonly ILogger<DockerPoolService>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized = false;

    public event EventHandler? OnStatusChanged;

    public DockerPoolService(
        IDockerService dockerService,
        IContainerStorage containerStorage,
        CodePodConfig config,
        ILogger<DockerPoolService>? logger = null)
    {
        _dockerService = dockerService;
        _containerStorage = containerStorage;
        _config = config;
        _logger = logger;
    }

    public async Task EnsurePrewarmAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        _logger?.LogInformation("Initializing Docker pool...");

        // 确保镜像存在
        await _dockerService.EnsureImageAsync(cancellationToken);

        // 清理旧的受管理容器
        var existingContainers = await _dockerService.GetManagedContainersAsync(cancellationToken);
        foreach (var container in existingContainers)
        {
            _logger?.LogInformation("Cleaning up leftover container {ContainerId}", container.ShortId);
            try
            {
                await _dockerService.DeleteContainerAsync(container.ContainerId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clean up container: {ContainerId}", container.ShortId);
            }
        }

        // 预热容器
        _logger?.LogInformation("Starting to warm {Count} containers...", _config.PrewarmCount);
        var prewarmTasks = new List<Task>();
        for (int i = 0; i < _config.PrewarmCount && i < _config.MaxContainers; i++)
        {
            prewarmTasks.Add(CreateAndWarmContainerAsync(cancellationToken));
        }
        await Task.WhenAll(prewarmTasks);

        _initialized = true;
        var containerCount = await _containerStorage.GetCountAsync(cancellationToken);
        _logger?.LogInformation("Docker pool initialization completed, warmed {Count} containers", containerCount);
        NotifyStatusChanged();
    }

    public async Task<ContainerInfo?> AcquireContainerAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // 查找空闲容器
            var idleContainer = await _containerStorage.GetFirstIdleAsync(cancellationToken);
            if (idleContainer != null)
            {
                idleContainer.Status = ContainerStatus.Busy;
                idleContainer.SessionId = sessionId;
                await _containerStorage.SaveAsync(idleContainer, cancellationToken);
                await _dockerService.AssignSessionToContainerAsync(idleContainer.ContainerId, sessionId, cancellationToken);
                _logger?.LogInformation("Allocated container {ContainerId} to session {SessionId}", idleContainer.ShortId, sessionId);
                NotifyStatusChanged();

                // 在后台补充预热容器（如果还有空间）
                _ = TryPrewarmOneAsync(CancellationToken.None);

                return idleContainer;
            }

            // 检查是否达到最大容器数（排除正在销毁的容器）
            var (idleCount, busyCount, warmingCount, _) = await _containerStorage.GetCountByStatusAsync(cancellationToken);
            var activeContainerCount = idleCount + busyCount + warmingCount;
            if (activeContainerCount >= _config.MaxContainers)
            {
                _logger?.LogWarning("Max container count {Max} reached (active: {Active}), cannot allocate", _config.MaxContainers, activeContainerCount);
                return null;
            }

            // 创建新容器并立即分配
            var newContainer = await CreateAndWarmContainerAsync(cancellationToken);
            newContainer.Status = ContainerStatus.Busy;
            newContainer.SessionId = sessionId;
            await _containerStorage.SaveAsync(newContainer, cancellationToken);
            await _dockerService.AssignSessionToContainerAsync(newContainer.ContainerId, sessionId, cancellationToken);
            _logger?.LogInformation("Created and allocated new container {ContainerId} to session {SessionId}", newContainer.ShortId, sessionId);
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
                var (idleCount, busyCount, warmingCount, _) = await _containerStorage.GetCountByStatusAsync(cancellationToken);
                var activeCount = idleCount + busyCount + warmingCount;

                var currentAvailable = idleCount + warmingCount;
                var maxCanCreate = _config.MaxContainers - activeCount;
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

            var tasks = new List<Task>();
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
            var container = await _containerStorage.GetAsync(containerId, cancellationToken);
            if (container != null)
            {
                container.Status = ContainerStatus.Destroying;
                await _containerStorage.SaveAsync(container, cancellationToken);
                NotifyStatusChanged();
            }

            await _containerStorage.DeleteAsync(containerId, cancellationToken);
            await _dockerService.DeleteContainerAsync(containerId, cancellationToken);
            _logger?.LogInformation("Released and deleted container {ContainerId}", containerId[..Math.Min(12, containerId.Length)]);
            NotifyStatusChanged();
        }
        finally
        {
            _lock.Release();
        }

        // 在后台补充预热容器
        _ = TryPrewarmOneAsync(CancellationToken.None);
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
            var (idleCount, busyCount, warmingCount, _) = await _containerStorage.GetCountByStatusAsync(cancellationToken);
            var activeCount = idleCount + busyCount + warmingCount;
            if (activeCount >= _config.MaxContainers)
            {
                throw new InvalidOperationException($"Max container count {_config.MaxContainers} reached");
            }

            var container = await CreateAndWarmContainerAsync(cancellationToken);
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
            var containers = await _containerStorage.GetAllAsync(cancellationToken);
            
            // 标记所有容器为销毁中
            foreach (var container in containers)
            {
                container.Status = ContainerStatus.Destroying;
                await _containerStorage.SaveAsync(container, cancellationToken);
            }
            NotifyStatusChanged();

            // 并行删除所有容器
            var deleteTasks = containers.Select(async c =>
            {
                try
                {
                    await _dockerService.DeleteContainerAsync(c.ContainerId, cancellationToken);
                    await _containerStorage.DeleteAsync(c.ContainerId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete container: {ContainerId}", c.ShortId);
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
        var containers = await _containerStorage.GetAllAsync(cancellationToken);
        return containers.ToList();
    }

    private async Task<ContainerInfo> CreateAndWarmContainerAsync(CancellationToken cancellationToken)
    {
        // 创建占位容器信息（预热中状态）
        var tempId = Guid.NewGuid().ToString("N");
        var warmingContainer = new ContainerInfo
        {
            ContainerId = tempId,
            Name = "warming...",
            Image = _config.Image,
            DockerStatus = "creating",
            Status = ContainerStatus.Warming,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _containerStorage.SaveAsync(warmingContainer, cancellationToken);
        NotifyStatusChanged();

        try
        {
            // 创建实际容器
            var containerInfo = await _dockerService.CreateContainerAsync(null, true, cancellationToken);

            // 等待容器进入running状态
            var maxWait = TimeSpan.FromSeconds(30);
            var waited = TimeSpan.Zero;
            var pollInterval = TimeSpan.FromMilliseconds(500);

            while (waited < maxWait)
            {
                var inspected = await _dockerService.GetContainerAsync(containerInfo.ContainerId, cancellationToken);
                if (inspected != null && inspected.DockerStatus == "running")
                {
                    break;
                }
                await Task.Delay(pollInterval, cancellationToken);
                waited += pollInterval;
            }

            // 执行一个简单命令确保容器就绪
            await _dockerService.ExecuteCommandAsync(containerInfo.ContainerId, "echo ready", "/app", 10, cancellationToken);

            // 移除临时占位，添加真实容器
            await _containerStorage.DeleteAsync(tempId, cancellationToken);

            var readyContainer = new ContainerInfo
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

            await _containerStorage.SaveAsync(readyContainer, cancellationToken);
            _logger?.LogInformation("Container {ContainerId} warmed up", readyContainer.ShortId);
            NotifyStatusChanged();

            return readyContainer;
        }
        catch
        {
            await _containerStorage.DeleteAsync(tempId, cancellationToken);
            NotifyStatusChanged();
            throw;
        }
    }

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
}
