using DockerShellHost.Configuration;
using DockerShellHost.Models;
using DockerShellHost.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DockerShellHost.Controllers;

/// <summary>
/// 管理员API - 管理Docker容器
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly IDockerPoolService _poolService;
    private readonly ISessionService _sessionService;
    private readonly IDockerService _dockerService;
    private readonly ILogger<AdminController> _logger;
    private readonly DockerPoolConfig _config;

    public AdminController(
        IDockerPoolService poolService,
        ISessionService sessionService,
        IDockerService dockerService,
        IOptions<DockerPoolConfig> config,
        ILogger<AdminController> logger)
    {
        _poolService = poolService;
        _sessionService = sessionService;
        _dockerService = dockerService;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// 获取系统状态
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<SystemStatus>>> GetSystemStatus(CancellationToken cancellationToken)
    {
        var sessions = _sessionService.GetAllSessions().ToList();
        var containers = await _poolService.GetAllContainersAsync(cancellationToken);

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

        return Ok(ApiResponse<SystemStatus>.Ok(status));
    }

    /// <summary>
    /// 获取所有容器
    /// </summary>
    [HttpGet("containers")]
    public async Task<ActionResult<ApiResponse<List<ContainerInfo>>>> GetContainers(CancellationToken cancellationToken)
    {
        var containers = await _poolService.GetAllContainersAsync(cancellationToken);
        return Ok(ApiResponse<List<ContainerInfo>>.Ok(containers));
    }

    /// <summary>
    /// 获取容器详情
    /// </summary>
    [HttpGet("containers/{containerId}")]
    public async Task<ActionResult<ApiResponse<ContainerInfo>>> GetContainer(string containerId, CancellationToken cancellationToken)
    {
        var container = await _dockerService.GetContainerAsync(containerId, cancellationToken);
        if (container == null)
        {
            return NotFound(ApiResponse<ContainerInfo>.Fail("Container not found"));
        }
        return Ok(ApiResponse<ContainerInfo>.Ok(container));
    }

    /// <summary>
    /// 创建新的预热容器
    /// </summary>
    [HttpPost("containers")]
    public async Task<ActionResult<ApiResponse<ContainerInfo>>> CreateContainer(CancellationToken cancellationToken)
    {
        try
        {
            var container = await _poolService.CreateContainerAsync(cancellationToken);
            _logger.LogInformation("Admin created container {ContainerId}", container.ShortId);
            return Ok(ApiResponse<ContainerInfo>.Ok(container));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create container");
            return BadRequest(ApiResponse<ContainerInfo>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 删除容器
    /// </summary>
    [HttpDelete("containers/{containerId}")]
    public async Task<ActionResult<ApiResponse>> DeleteContainer(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            // 通知会话服务
            _sessionService.OnContainerDeleted(containerId);

            await _poolService.ForceDeleteContainerAsync(containerId, cancellationToken);
            _logger.LogInformation("Admin deleted container {ContainerId}", containerId[..Math.Min(12, containerId.Length)]);
            return Ok(ApiResponse.Ok("Container deleted"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete container: {ContainerId}", containerId);
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 删除所有受管理的容器
    /// </summary>
    [HttpDelete("containers")]
    public async Task<ActionResult<ApiResponse>> DeleteAllContainers(CancellationToken cancellationToken)
    {
        try
        {
            await _poolService.DeleteAllContainersAsync(cancellationToken);
            _logger.LogInformation("Admin deleted all managed containers");
            return Ok(ApiResponse.Ok("All containers deleted"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all containers");
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 获取所有会话
    /// </summary>
    [HttpGet("sessions")]
    public ActionResult<ApiResponse<List<SessionInfo>>> GetAllSessions()
    {
        var sessions = _sessionService.GetAllSessions().ToList();
        return Ok(ApiResponse<List<SessionInfo>>.Ok(sessions));
    }

    /// <summary>
    /// 强制销毁会话
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    public async Task<ActionResult<ApiResponse>> DestroySession(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await _sessionService.DestroySessionAsync(sessionId, cancellationToken);
            _logger.LogInformation("Admin destroyed session {SessionId}", sessionId);
            return Ok(ApiResponse.Ok("Session destroyed"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to destroy session: {SessionId}", sessionId);
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 触发预热
    /// </summary>
    [HttpPost("prewarm")]
    public async Task<ActionResult<ApiResponse>> TriggerPrewarm(CancellationToken cancellationToken)
    {
        try
        {
            await _poolService.EnsurePrewarmAsync(cancellationToken);
            return Ok(ApiResponse.Ok("Prewarm completed"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prewarm failed");
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }
}
