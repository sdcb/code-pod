using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;
using Microsoft.EntityFrameworkCore;
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
    private readonly IDbContextFactory<CodePodDbContext> _contextFactory;
    private readonly ISessionService _sessionService;
    private readonly CodePodConfig _config;
    private readonly ILogger<SessionCleanupService>? _logger;

    public SessionCleanupService(
        IDbContextFactory<CodePodDbContext> contextFactory,
        ISessionService sessionService,
        CodePodConfig config,
        ILogger<SessionCleanupService>? logger = null)
    {
        _contextFactory = contextFactory;
        _sessionService = sessionService;
        _config = config;
        _logger = logger;
    }

    public async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        List<SessionEntity> activeSessions;
        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            activeSessions = await context.Sessions
                .Where(s => s.Status != SessionStatus.Destroyed)
                .ToListAsync(cancellationToken);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (SessionEntity session in activeSessions)
        {
            // 跳过正在执行命令的会话
            if (session.IsExecutingCommand)
            {
                continue;
            }

            // 计算超时时间
            int timeoutSeconds = session.TimeoutSeconds ?? _config.SessionTimeoutSeconds;
            TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);

            if (now - session.LastActivityAt > timeout)
            {
                _logger?.LogInformation("Session {SessionId} expired (timeout: {Timeout}s, last activity: {LastActivity})",
                    session.Id, timeoutSeconds, session.LastActivityAt);

                try
                {
                    await _sessionService.DestroySessionAsync(session.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to cleanup expired session {SessionId}", session.Id);
                }
            }
        }
    }
}
