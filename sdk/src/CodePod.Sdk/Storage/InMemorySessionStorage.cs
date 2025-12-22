using System.Collections.Concurrent;
using CodePod.Sdk.Models;

namespace CodePod.Sdk.Storage;

/// <summary>
/// 内存会话存储实现
/// </summary>
public class InMemorySessionStorage : ISessionStorage
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();

    public Task SaveAsync(SessionInfo session, CancellationToken cancellationToken = default)
    {
        _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<IReadOnlyList<SessionInfo>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var result = _sessions.Values
            .Where(s => s.Status != SessionStatus.Destroyed)
            .ToList();
        return Task.FromResult<IReadOnlyList<SessionInfo>>(result);
    }

    public Task<IReadOnlyList<SessionInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = _sessions.Values.ToList();
        return Task.FromResult<IReadOnlyList<SessionInfo>>(result);
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<SessionInfo?> GetByContainerIdAsync(string containerId, CancellationToken cancellationToken = default)
    {
        var session = _sessions.Values.FirstOrDefault(s => s.ContainerId == containerId);
        return Task.FromResult(session);
    }

    public Task<IReadOnlyList<SessionInfo>> GetQueuedSessionsAsync(CancellationToken cancellationToken = default)
    {
        var result = _sessions.Values
            .Where(s => s.Status == SessionStatus.Queued)
            .OrderBy(s => s.QueuePosition)
            .ToList();
        return Task.FromResult<IReadOnlyList<SessionInfo>>(result);
    }
}
