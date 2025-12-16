using System.Collections.Concurrent;
using DockerShellHost.Configuration;
using DockerShellHost.Models;
using Microsoft.Extensions.Options;

namespace DockerShellHost.Services;

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
    private readonly DockerPoolConfig _config;
    private readonly ILogger<DockerPoolService> _logger;
    private readonly ConcurrentDictionary<string, ContainerInfo> _containers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized = false;

    public event EventHandler? OnStatusChanged;

    public DockerPoolService(
        IDockerService dockerService,
        IOptions<DockerPoolConfig> config,
        ILogger<DockerPoolService> logger)
    {
        _dockerService = dockerService;
        _config = config.Value;
        _logger = logger;
    }

    public async Task EnsurePrewarmAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        _logger.LogInformation("Initializing Docker pool...");

        // 确保镜像存在
        await _dockerService.EnsureImageAsync(cancellationToken);

        // 清理旧的受管理容器
        var existingContainers = await _dockerService.GetManagedContainersAsync(cancellationToken);
        foreach (var container in existingContainers)
        {
            _logger.LogInformation("Cleaning up leftover container {ContainerId}", container.ShortId);
            try
            {
                await _dockerService.DeleteContainerAsync(container.ContainerId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up container: {ContainerId}", container.ShortId);
            }
        }

        // 预热容器
        _logger.LogInformation("Starting to warm {Count} containers...", _config.PrewarmCount);
        var prewarmTasks = new List<Task>();
        for (int i = 0; i < _config.PrewarmCount && i < _config.MaxContainers; i++)
        {
            prewarmTasks.Add(CreateAndWarmContainerAsync(cancellationToken));
        }
        await Task.WhenAll(prewarmTasks);

        _initialized = true;
        _logger.LogInformation("Docker pool initialization completed, warmed {Count} containers", _containers.Count);
        NotifyStatusChanged();
    }

    public async Task<ContainerInfo?> AcquireContainerAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // 查找空闲容器
            var idleContainer = _containers.Values.FirstOrDefault(c => c.Status == ContainerStatus.Idle);
            if (idleContainer != null)
            {
                idleContainer.Status = ContainerStatus.Busy;
                idleContainer.SessionId = sessionId;
                await _dockerService.AssignSessionToContainerAsync(idleContainer.ContainerId, sessionId, cancellationToken);
                _logger.LogInformation("Allocated container {ContainerId} to session {SessionId}", idleContainer.ShortId, sessionId);
                NotifyStatusChanged();

                // 在后台补充预热容器（如果还有空间）
                _ = TryPrewarmOneAsync(CancellationToken.None);

                return idleContainer;
            }

            // 检查是否达到最大容器数
            if (_containers.Count >= _config.MaxContainers)
            {
                _logger.LogWarning("Max container count {Max} reached, cannot allocate", _config.MaxContainers);
                return null;
            }

            // 创建新容器并立即分配
            var newContainer = await CreateAndWarmContainerAsync(cancellationToken);
            newContainer.Status = ContainerStatus.Busy;
            newContainer.SessionId = sessionId;
            await _dockerService.AssignSessionToContainerAsync(newContainer.ContainerId, sessionId, cancellationToken);
            _logger.LogInformation("Created and allocated new container {ContainerId} to session {SessionId}", newContainer.ShortId, sessionId);
            NotifyStatusChanged();
            return newContainer;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 尝试在后台预热容器，补充到目标数量
    /// </summary>
    private async Task TryPrewarmOneAsync(CancellationToken cancellationToken)
    {
        try
        {
            int neededCount;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                // 检查需要预热多少个
                var idleCount = _containers.Values.Count(c => c.Status == ContainerStatus.Idle);
                var warmingCount = _containers.Values.Count(c => c.Status == ContainerStatus.Warming);
                var totalCount = _containers.Count;

                // 计算需要预热的数量
                var currentAvailable = idleCount + warmingCount;
                var maxCanCreate = _config.MaxContainers - totalCount;
                neededCount = Math.Min(_config.PrewarmCount - currentAvailable, maxCanCreate);

                if (neededCount <= 0)
                {
                    return;
                }

                _logger.LogInformation("Background prewarm: idle: {Idle}, warming: {Warming}, need to create: {Needed}", 
                    idleCount, warmingCount, neededCount);
            }
            finally
            {
                _lock.Release();
            }

            // 在锁外并行创建容器
            var tasks = new List<Task>();
            for (int i = 0; i < neededCount; i++)
            {
                tasks.Add(CreateAndWarmContainerAsync(cancellationToken));
            }
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background prewarm failed");
        }
    }

    public async Task ReleaseContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_containers.TryGetValue(containerId, out var container))
            {
                container.Status = ContainerStatus.Destroying;
                NotifyStatusChanged();
            }

            if (_containers.TryRemove(containerId, out _))
            {
                await _dockerService.DeleteContainerAsync(containerId, cancellationToken);
                _logger.LogInformation("Released and deleted container {ContainerId}", containerId[..Math.Min(12, containerId.Length)]);
            }
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
            if (_containers.Count >= _config.MaxContainers)
            {
                throw new InvalidOperationException($"Max container count {_config.MaxContainers} reached");
            }

            var container = await CreateAndWarmContainerAsync(cancellationToken);
            _logger.LogInformation("Manually created container {ContainerId}", container.ShortId);
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
            // 标记所有容器为销毁中
            foreach (var container in _containers.Values)
            {
                container.Status = ContainerStatus.Destroying;
            }
            NotifyStatusChanged();

            // 并行删除所有容器
            var deleteTasks = _containers.Keys.ToList().Select(async id =>
            {
                try
                {
                    await _dockerService.DeleteContainerAsync(id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete container: {ContainerId}", id[..Math.Min(12, id.Length)]);
                }
            });
            await Task.WhenAll(deleteTasks);

            _containers.Clear();
            _logger.LogInformation("All containers deleted");
            NotifyStatusChanged();
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<List<ContainerInfo>> GetAllContainersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_containers.Values.ToList());
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
        _containers[tempId] = warmingContainer;
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
            _containers.TryRemove(tempId, out _);

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

            _containers[containerInfo.ContainerId] = readyContainer;
            _logger.LogInformation("Container {ContainerId} warmed up", readyContainer.ShortId);
            NotifyStatusChanged();

            return readyContainer;
        }
        catch
        {
            _containers.TryRemove(tempId, out _);
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
            _logger.LogWarning(ex, "Status change notification failed");
        }
    }
}
