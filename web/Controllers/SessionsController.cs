using DockerShellHost.Models;
using DockerShellHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace DockerShellHost.Controllers;

/// <summary>
/// 会话API - 会话管理
/// </summary>
[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly ILogger<SessionsController> _logger;

    public SessionsController(
        ISessionService sessionService,
        ILogger<SessionsController> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有会话
    /// </summary>
    [HttpGet]
    public ActionResult<ApiResponse<List<SessionInfo>>> GetSessions()
    {
        var sessions = _sessionService.GetAllSessions().ToList();
        return Ok(ApiResponse<List<SessionInfo>>.Ok(sessions));
    }

    /// <summary>
    /// 创建新会话
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<SessionInfo>>> CreateSession(
        [FromBody] CreateSessionRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            // 验证超时时间
            if (request?.TimeoutSeconds != null)
            {
                if (request.TimeoutSeconds <= 0)
                {
                    return BadRequest(ApiResponse<SessionInfo>.Fail(
                        "TimeoutSeconds must be greater than 0"));
                }
                if (request.TimeoutSeconds > _sessionService.MaxTimeoutSeconds)
                {
                    return BadRequest(ApiResponse<SessionInfo>.Fail(
                        $"TimeoutSeconds cannot exceed the system limit of {_sessionService.MaxTimeoutSeconds} seconds"));
                }
            }

            var session = await _sessionService.CreateSessionAsync(
                request?.Name, 
                request?.TimeoutSeconds, 
                cancellationToken);
            _logger.LogInformation("Session {SessionId} created with timeout {Timeout}s", 
                session.SessionId, session.TimeoutSeconds ?? _sessionService.MaxTimeoutSeconds);
            return Ok(ApiResponse<SessionInfo>.Ok(session));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session");
            return BadRequest(ApiResponse<SessionInfo>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 获取会话详情
    /// </summary>
    [HttpGet("{sessionId}")]
    public ActionResult<ApiResponse<SessionInfo>> GetSession(string sessionId)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound(ApiResponse<SessionInfo>.Fail("Session not found"));
        }
        return Ok(ApiResponse<SessionInfo>.Ok(session));
    }

    /// <summary>
    /// 销毁会话
    /// </summary>
    [HttpDelete("{sessionId}")]
    public async Task<ActionResult<ApiResponse>> DestroySession(string sessionId, CancellationToken cancellationToken)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound(ApiResponse.Fail("Session not found"));
        }

        try
        {
            await _sessionService.DestroySessionAsync(sessionId, cancellationToken);
            _logger.LogInformation("Session {SessionId} destroyed", sessionId);
            return Ok(ApiResponse.Ok("Session destroyed"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to destroy session: {SessionId}", sessionId);
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }
}
