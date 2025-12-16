using DockerShellHost.Models;
using DockerShellHost.Services;
using Microsoft.AspNetCore.Mvc;

namespace DockerShellHost.Controllers;

/// <summary>
/// 文件操作API - 在会话中进行文件操作
/// </summary>
[ApiController]
[Route("api/sessions/{sessionId}/files")]
public class FilesController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly IDockerService _dockerService;
    private readonly ILogger<FilesController> _logger;

    public FilesController(
        ISessionService sessionService,
        IDockerService dockerService,
        ILogger<FilesController> logger)
    {
        _sessionService = sessionService;
        _dockerService = dockerService;
        _logger = logger;
    }

    /// <summary>
    /// 列出目录内容
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<ApiResponse<DirectoryListResponse>>> ListDirectory(
        string sessionId,
        [FromQuery] string path = "/app",
        CancellationToken cancellationToken = default)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound(ApiResponse<DirectoryListResponse>.Fail("Session not found"));
        }

        if (session.Status != SessionStatus.Active || string.IsNullOrEmpty(session.ContainerId))
        {
            return BadRequest(ApiResponse<DirectoryListResponse>.Fail("Session is not active or has no associated container"));
        }

        try
        {
            var entries = await _dockerService.ListDirectoryAsync(session.ContainerId, path, cancellationToken);
            _sessionService.UpdateSessionActivity(sessionId);

            var response = new DirectoryListResponse
            {
                Path = path,
                Entries = entries,
                TotalCount = entries.Count
            };

            return Ok(ApiResponse<DirectoryListResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list directory: {Path}", path);
            return BadRequest(ApiResponse<DirectoryListResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 上传文件（表单方式）
    /// </summary>
    [HttpPost("upload")]
    public async Task<ActionResult<ApiResponse<UploadFileResponse>>> UploadFile(
        string sessionId,
        [FromQuery] string targetPath,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound(ApiResponse<UploadFileResponse>.Fail("Session not found"));
        }

        if (session.Status != SessionStatus.Active || string.IsNullOrEmpty(session.ContainerId))
        {
            return BadRequest(ApiResponse<UploadFileResponse>.Fail("Session is not active or has no associated container"));
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest(ApiResponse<UploadFileResponse>.Fail("No file uploaded"));
        }

        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, cancellationToken);
            var content = ms.ToArray();

            var fullPath = Path.Combine(targetPath ?? "/app", file.FileName).Replace('\\', '/');

            // 确保目标目录存在
            var directory = Path.GetDirectoryName(fullPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
            {
                await _dockerService.ExecuteCommandAsync(
                    session.ContainerId,
                    $"mkdir -p {directory}",
                    "/",
                    10,
                    cancellationToken);
            }

            await _dockerService.UploadFileAsync(session.ContainerId, fullPath, content, cancellationToken);
            _sessionService.UpdateSessionActivity(sessionId);

            _logger.LogInformation("Session {SessionId} uploaded file: {Path} ({Size} bytes)",
                sessionId, fullPath, content.Length);

            return Ok(ApiResponse<UploadFileResponse>.Ok(new UploadFileResponse
            {
                Success = true,
                FilePath = fullPath
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file");
            return BadRequest(ApiResponse<UploadFileResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 下载文件（返回文件流，用于浏览器下载）
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> DownloadFileRaw(
        string sessionId,
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound("Session not found");
        }

        if (session.Status != SessionStatus.Active || string.IsNullOrEmpty(session.ContainerId))
        {
            return BadRequest("Session is not active or has no associated container");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("File path cannot be empty");
        }

        try
        {
            var content = await _dockerService.DownloadFileAsync(session.ContainerId, path, cancellationToken);
            _sessionService.UpdateSessionActivity(sessionId);

            var fileName = Path.GetFileName(path);
            var contentType = GetContentType(fileName);

            _logger.LogInformation("Session {SessionId} downloaded file: {Path} ({Size} bytes)",
                sessionId, path, content.Length);

            return File(content, contentType, fileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound($"File not found: {path}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file: {Path}", path);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// 删除文件或目录
    /// </summary>
    [HttpDelete]
    public async Task<ActionResult<ApiResponse<object>>> DeleteFile(
        string sessionId,
        [FromQuery] string path,
        CancellationToken cancellationToken = default)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound(ApiResponse<object>.Fail("Session not found"));
        }

        if (session.Status != SessionStatus.Active || string.IsNullOrEmpty(session.ContainerId))
        {
            return BadRequest(ApiResponse<object>.Fail("Session is not active or has no associated container"));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(ApiResponse<object>.Fail("Path cannot be empty"));
        }

        try
        {
            // 使用 rm -rf 删除文件或目录
            var result = await _dockerService.ExecuteCommandAsync(
                session.ContainerId,
                $"rm -rf {path}",
                "/",
                30,
                cancellationToken);

            if (result.ExitCode != 0)
            {
                return BadRequest(ApiResponse<object>.Fail($"Delete failed: {result.Stderr}"));
            }

            _sessionService.UpdateSessionActivity(sessionId);
            _logger.LogInformation("Session {SessionId} deleted file/directory: {Path}", sessionId, path);

            return Ok(ApiResponse<object>.Ok(new { path, deleted = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file: {Path}", path);
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// 获取文件的 Content-Type
    /// </summary>
    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" => "text/html",
            ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".csv" => "text/csv",
            ".md" => "text/markdown",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".zip" => "application/zip",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".cs" => "text/plain",
            ".py" => "text/plain",
            ".sh" => "text/plain",
            ".yaml" => "text/yaml",
            ".yml" => "text/yaml",
            _ => "application/octet-stream"
        };
    }
}
