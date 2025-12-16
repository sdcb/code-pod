using Microsoft.AspNetCore.SignalR;
using DockerShellHost.Models;

namespace DockerShellHost.Hubs;

/// <summary>
/// SignalR Hub 用于实时推送系统状态
/// </summary>
public class StatusHub : Hub<IStatusHubClient>
{
    private readonly ILogger<StatusHub> _logger;

    public StatusHub(ILogger<StatusHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// SignalR 客户端接口定义
/// </summary>
public interface IStatusHubClient
{
    /// <summary>
    /// 接收系统状态更新
    /// </summary>
    Task StatusUpdated(SystemStatus status);

    /// <summary>
    /// 接收容器状态变更通知
    /// </summary>
    Task ContainerStatusChanged(ContainerInfo container);
}
