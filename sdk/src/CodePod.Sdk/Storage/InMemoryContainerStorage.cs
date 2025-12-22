using System.Collections.Concurrent;
using CodePod.Sdk.Models;

namespace CodePod.Sdk.Storage;

/// <summary>
/// 内存容器存储实现
/// </summary>
public class InMemoryContainerStorage : IContainerStorage
{
    private readonly ConcurrentDictionary<string, ContainerInfo> _containers = new();

    public Task SaveAsync(ContainerInfo container, CancellationToken cancellationToken = default)
    {
        _containers[container.ContainerId] = container;
        return Task.CompletedTask;
    }

    public Task<ContainerInfo?> GetAsync(string containerId, CancellationToken cancellationToken = default)
    {
        _containers.TryGetValue(containerId, out var container);
        return Task.FromResult(container);
    }

    public Task<IReadOnlyList<ContainerInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = _containers.Values.ToList();
        return Task.FromResult<IReadOnlyList<ContainerInfo>>(result);
    }

    public Task DeleteAsync(string containerId, CancellationToken cancellationToken = default)
    {
        _containers.TryRemove(containerId, out _);
        return Task.CompletedTask;
    }

    public Task<ContainerInfo?> GetFirstIdleAsync(CancellationToken cancellationToken = default)
    {
        var container = _containers.Values.FirstOrDefault(c => c.Status == ContainerStatus.Idle);
        return Task.FromResult(container);
    }

    public Task<(int idle, int busy, int warming, int destroying)> GetCountByStatusAsync(CancellationToken cancellationToken = default)
    {
        var containers = _containers.Values.ToList();
        var idle = containers.Count(c => c.Status == ContainerStatus.Idle);
        var busy = containers.Count(c => c.Status == ContainerStatus.Busy);
        var warming = containers.Count(c => c.Status == ContainerStatus.Warming);
        var destroying = containers.Count(c => c.Status == ContainerStatus.Destroying);
        return Task.FromResult((idle, busy, warming, destroying));
    }

    public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_containers.Count);
    }
}
