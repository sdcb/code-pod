using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DockerShellHost.Models;
using DockerShellHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace DockerShellHost.Controllers;

/// <summary>
/// 命令执行API - 在会话中执行命令
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId}/commands")]
public class CommandsController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly IDockerService _dockerService;
    private readonly ILogger<CommandsController> _logger;

    public CommandsController(
        ISessionService sessionService,
        IDockerService dockerService,
        ILogger<CommandsController> logger)
    {
        _sessionService = sessionService;
        _dockerService = dockerService;
        _logger = logger;
    }

    /// <summary>
    /// 执行Shell命令（非流式响应）
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CommandResult>>> ExecuteCommand(
        string sessionId,
        [FromBody] CommandRequest request,
        CancellationToken cancellationToken)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound(ApiResponse<CommandResult>.Fail("Session not found"));
        }

        if (session.Status != SessionStatus.Active)
        {
            return BadRequest(ApiResponse<CommandResult>.Fail($"Session is not active: {session.Status}"));
        }

        if (string.IsNullOrEmpty(session.ContainerId))
        {
            return BadRequest(ApiResponse<CommandResult>.Fail("Session has no associated container"));
        }

        try
        {
            _logger.LogInformation("Session {SessionId} executing command: {Command}", sessionId, request.Command);

            // 标记会话正在执行命令，防止被超时清理
            _sessionService.SetExecutingCommand(sessionId, true);

            var result = await _dockerService.ExecuteCommandAsync(
                session.ContainerId,
                request.Command,
                request.WorkingDirectory,
                request.TimeoutSeconds,
                cancellationToken);

            // 更新会话活动时间
            _sessionService.UpdateSessionActivity(sessionId);
            _sessionService.IncrementCommandCount(sessionId);

            _logger.LogInformation("Command execution completed, exit code: {ExitCode}, time: {Time}ms",
                result.ExitCode, result.ExecutionTimeMs);

            return Ok(ApiResponse<CommandResult>.Ok(result));
        }
        catch (OperationCanceledException)
        {
            return BadRequest(ApiResponse<CommandResult>.Fail("Command execution timed out"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command execution failed: {SessionId}", sessionId);
            return BadRequest(ApiResponse<CommandResult>.Fail(ex.Message));
        }
        finally
        {
            // 无论成功失败，都清除执行状态
            _sessionService.SetExecutingCommand(sessionId, false);
        }
    }

    /// <summary>
    /// 执行Shell命令（SSE 流式响应）
    /// </summary>
    /// <remarks>
    /// 返回 Server-Sent Events 流，事件类型包括：
    /// - stdout: 标准输出数据
    /// - stderr: 标准错误数据
    /// - exit: 命令执行完成，包含退出码和执行时间
    /// </remarks>
    [HttpPost("stream")]
    public IResult ExecuteCommandStream(
        string sessionId,
        [FromBody] CommandRequest request,
        CancellationToken cancellationToken)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return Results.NotFound(ApiResponse<CommandResult>.Fail("Session not found"));
        }

        if (session.Status != SessionStatus.Active)
        {
            return Results.BadRequest(ApiResponse<CommandResult>.Fail($"Session is not active: {session.Status}"));
        }

        if (string.IsNullOrEmpty(session.ContainerId))
        {
            return Results.BadRequest(ApiResponse<CommandResult>.Fail("Session has no associated container"));
        }

        _logger.LogInformation("Session {SessionId} executing command (streaming): {Command}", sessionId, request.Command);

        return TypedResults.ServerSentEvents(
            StreamCommandOutputAsync(sessionId, session.ContainerId, request, cancellationToken));
    }

    private async IAsyncEnumerable<SseItem<CommandOutputEvent>> StreamCommandOutputAsync(
        string sessionId,
        string containerId,
        CommandRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 标记会话正在执行命令
        _sessionService.SetExecutingCommand(sessionId, true);

        try
        {
            await foreach (var outputEvent in _dockerService.ExecuteCommandStreamAsync(
                containerId,
                request.Command,
                request.WorkingDirectory,
                request.TimeoutSeconds,
                cancellationToken))
            {
                var eventType = outputEvent.Type switch
                {
                    CommandOutputType.Stdout => "stdout",
                    CommandOutputType.Stderr => "stderr",
                    CommandOutputType.Exit => "exit",
                    _ => "message"
                };

                yield return new SseItem<CommandOutputEvent>(outputEvent, eventType);

                // 如果是退出事件，更新会话统计
                if (outputEvent.Type == CommandOutputType.Exit)
                {
                    _sessionService.UpdateSessionActivity(sessionId);
                    _sessionService.IncrementCommandCount(sessionId);

                    _logger.LogInformation("Command execution completed (streaming), exit code: {ExitCode}, time: {Time}ms",
                        outputEvent.ExitCode, outputEvent.ExecutionTimeMs);
                }
            }
        }
        finally
        {
            // 清除执行状态
            _sessionService.SetExecutingCommand(sessionId, false);
        }
    }
}
