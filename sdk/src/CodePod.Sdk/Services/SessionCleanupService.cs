using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk.Services;

/// <summary>
/// 会话清理服务接口
/// </summary>
public interface ISessionCleanupService
{
    /// <summary>
    /// 清理超时会话
    /// </summary>
    Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话清理服务实现
/// </summary>
public class SessionCleanupService : ISessionCleanupService
{
    private readonly ISessionStorage _sessionStorage;
    private readonly ISessionService _sessionService;
    private readonly CodePodConfig _config;
    private readonly ILogger<SessionCleanupService>? _logger;

    public SessionCleanupService(
        ISessionStorage sessionStorage,
        ISessionService sessionService,
        CodePodConfig config,
        ILogger<SessionCleanupService>? logger = null)
    {
        _sessionStorage = sessionStorage;
        _sessionService = sessionService;
        _config = config;
        _logger = logger;
    }

    public async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _sessionStorage.GetAllActiveAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var session in sessions)
        {
            // 跳过正在执行命令的会话
            if (session.IsExecutingCommand)
            {
                continue;
            }

            // 计算超时时间
            var timeoutSeconds = session.TimeoutSeconds ?? _config.SessionTimeoutSeconds;
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);

            if (now - session.LastActivityAt > timeout)
            {
                _logger?.LogInformation("Session {SessionId} expired (timeout: {Timeout}s, last activity: {LastActivity})",
                    session.SessionId, timeoutSeconds, session.LastActivityAt);
                
                try
                {
                    await _sessionService.DestroySessionAsync(session.SessionId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to cleanup expired session {SessionId}", session.SessionId);
                }
            }
        }
    }
}
