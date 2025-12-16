using DockerShellHost.Configuration;
using DockerShellHost.Hubs;
using DockerShellHost.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DockerShellHost.Services;

/// <summary>
/// 预热服务 - 启动时自动预热容器并推送状态更新
/// </summary>
public class PrewarmService : BackgroundService
{
    private readonly IDockerPoolService _poolService;
    private readonly ISessionService _sessionService;
    private readonly IHubContext<StatusHub, IStatusHubClient> _hubContext;
    private readonly ILogger<PrewarmService> _logger;
    private readonly DockerPoolConfig _config;

    public PrewarmService(
        IDockerPoolService poolService,
        ISessionService sessionService,
        IHubContext<StatusHub, IStatusHubClient> hubContext,
        IOptions<DockerPoolConfig> config,
        ILogger<PrewarmService> logger)
    {
        _poolService = poolService;
        _sessionService = sessionService;
        _hubContext = hubContext;
        _config = config.Value;
        _logger = logger;

        // 订阅状态变化事件
        _poolService.OnStatusChanged += OnPoolStatusChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Prewarm service started, target container count: {Count}", _config.PrewarmCount);

        // 等待一小段时间让其他服务初始化
        await Task.Delay(1000, stoppingToken);

        try
        {
            // 自动预热
            await _poolService.EnsurePrewarmAsync(stoppingToken);
            _logger.LogInformation("Initial prewarm completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial prewarm failed");
        }

        // 保持运行以继续推送状态更新
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async void OnPoolStatusChanged(object? sender, EventArgs e)
    {
        try
        {
            var containers = await _poolService.GetAllContainersAsync(CancellationToken.None);
            var sessions = _sessionService.GetAllSessions().ToList();

            var status = new SystemStatus
            {
                MaxContainers = _config.MaxContainers,
                AvailableContainers = containers.Count(c => c.Status == ContainerStatus.Idle),
                ActiveSessions = sessions.Count(s => s.Status == SessionStatus.Active),
                WarmingContainers = containers.Count(c => c.Status == ContainerStatus.Warming),
                DestroyingContainers = containers.Count(c => c.Status == ContainerStatus.Destroying),
                Image = _config.Image,
                Containers = containers
            };

            // 推送到所有连接的客户端
            await _hubContext.Clients.All.StatusUpdated(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to push status update");
        }
    }

    public override void Dispose()
    {
        _poolService.OnStatusChanged -= OnPoolStatusChanged;
        base.Dispose();
    }
}
