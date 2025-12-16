using DockerShellHost.Configuration;
using Microsoft.Extensions.Options;

namespace DockerShellHost.Services;

/// <summary>
/// 后台服务：管理会话超时清理
/// </summary>
public class SessionCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly DockerPoolConfig _config;

    public SessionCleanupService(
        IServiceProvider serviceProvider,
        IOptions<DockerPoolConfig> config,
        ILogger<SessionCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while cleaning up expired sessions");
            }

            // 每秒检查一次，以便快速响应超时
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();

        var expiredSessions = sessionService.GetAllSessions()
            .Where(s => s.Status == Models.SessionStatus.Active &&
                        !s.IsExecutingCommand &&  // 跳过正在执行命令的会话
                        IsSessionExpired(s))
            .ToList();

        foreach (var session in expiredSessions)
        {
            var effectiveTimeout = session.TimeoutSeconds ?? _config.SessionTimeoutSeconds;
            _logger.LogInformation("Session {SessionId} timed out ({Seconds}s inactive), cleaning up...",
                session.SessionId, effectiveTimeout);

            await sessionService.DestroySessionAsync(session.SessionId, cancellationToken);
        }

        if (expiredSessions.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions", expiredSessions.Count);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Session cleanup service stopped");
        await base.StopAsync(cancellationToken);
    }

    private bool IsSessionExpired(Models.SessionInfo session)
    {
        // 使用会话级别的超时时间，如果未设置则使用系统配置
        var effectiveTimeout = session.TimeoutSeconds ?? _config.SessionTimeoutSeconds;
        return DateTimeOffset.UtcNow - session.LastActivityAt > TimeSpan.FromSeconds(effectiveTimeout);
    }
}
