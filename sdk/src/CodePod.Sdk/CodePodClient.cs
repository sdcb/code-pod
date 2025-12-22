using CodePod.Sdk.Configuration;
using CodePod.Sdk.Exceptions;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;

namespace CodePod.Sdk;

/// <summary>
/// CodePod SDK 主入口类
/// </summary>
public class CodePodClient : IDisposable
{
    private readonly Services.IDockerService _dockerService;
    private readonly Services.IDockerPoolService _poolService;
    private readonly Services.ISessionService _sessionService;
    private readonly Services.ISessionCleanupService _cleanupService;
    private readonly IContainerStorage _containerStorage;
    private readonly CodePodConfig _config;
    private bool _initialized;

    /// <summary>
    /// 创建 CodePodClient 实例
    /// </summary>
    public CodePodClient(
        Services.IDockerService dockerService,
        Services.IDockerPoolService poolService,
        Services.ISessionService sessionService,
        Services.ISessionCleanupService cleanupService,
        IContainerStorage containerStorage,
        CodePodConfig config)
    {
        _dockerService = dockerService;
        _poolService = poolService;
        _sessionService = sessionService;
        _cleanupService = cleanupService;
        _containerStorage = containerStorage;
        _config = config;
    }

    /// <summary>
    /// 初始化 SDK（预热容器等）
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _poolService.EnsurePrewarmAsync(cancellationToken);
        _initialized = true;
    }

    /// <summary>
    /// 获取系统状态
    /// </summary>
    public async Task<SystemStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (idle, busy, warming, destroying) = await _containerStorage.GetCountByStatusAsync(cancellationToken);
        var sessions = await _sessionService.GetAllSessionsAsync(cancellationToken);
        var sessionList = sessions.ToList();

        return new SystemStatus
        {
            AvailableContainers = idle,
            BusyContainers = busy,
            WarmingContainers = warming,
            DestroyingContainers = destroying,
            MaxContainers = _config.MaxContainers,
            ActiveSessions = sessionList.Count(s => s.Status == SessionStatus.Active),
            QueuedSessions = sessionList.Count(s => s.Status == SessionStatus.Queued)
        };
    }

    /// <summary>
    /// 获取最大超时时间
    /// </summary>
    public int MaxTimeoutSeconds => _sessionService.MaxTimeoutSeconds;

    /// <summary>
    /// 获取最大资源限制
    /// </summary>
    public ResourceLimits MaxResourceLimits => _config.MaxResourceLimits;

    /// <summary>
    /// 获取默认网络模式
    /// </summary>
    public NetworkMode DefaultNetworkMode => _config.DefaultNetworkMode;

    #region Session Operations

    /// <summary>
    /// 创建新会话
    /// </summary>
    public async Task<SessionInfo> CreateSessionAsync(string? name = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
    {
        return await _sessionService.CreateSessionAsync(name, timeoutSeconds, cancellationToken);
    }

    /// <summary>
    /// 创建新会话（带资源限制和网络模式）
    /// </summary>
    public async Task<SessionInfo> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken = default)
    {
        return await _sessionService.CreateSessionAsync(options, cancellationToken);
    }

    /// <summary>
    /// 获取会话详情
    /// </summary>
    public async Task<SessionInfo> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionService.GetSessionAsync(sessionId, cancellationToken);
        return session ?? throw new SessionNotFoundException(sessionId);
    }

    /// <summary>
    /// 获取所有会话
    /// </summary>
    public async Task<IReadOnlyList<SessionInfo>> GetAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _sessionService.GetAllSessionsAsync(cancellationToken);
        return sessions.ToList();
    }

    /// <summary>
    /// 销毁会话
    /// </summary>
    public async Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _sessionService.DestroySessionAsync(sessionId, cancellationToken);
    }

    #endregion

    #region Command Operations

    /// <summary>
    /// 在会话中执行命令
    /// </summary>
    public async Task<CommandResult> ExecuteCommandAsync(string sessionId, string command, string? workingDirectory = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        
        await _sessionService.SetExecutingCommandAsync(sessionId, true, cancellationToken);
        try
        {
            var result = await _dockerService.ExecuteCommandAsync(
                session.ContainerId!,
                command,
                workingDirectory ?? _config.WorkDir,
                timeoutSeconds ?? 60,
                cancellationToken);

            await _sessionService.IncrementCommandCountAsync(sessionId, cancellationToken);
            return result;
        }
        finally
        {
            await _sessionService.SetExecutingCommandAsync(sessionId, false, cancellationToken);
        }
    }

    /// <summary>
    /// 在会话中执行命令数组（直接执行，不经过shell包装）
    /// 例如: ["python", "-c", "print('hello')"]
    /// </summary>
    public async Task<CommandResult> ExecuteCommandAsync(string sessionId, string[] command, string? workingDirectory = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        
        await _sessionService.SetExecutingCommandAsync(sessionId, true, cancellationToken);
        try
        {
            var result = await _dockerService.ExecuteCommandAsync(
                session.ContainerId!,
                command,
                workingDirectory ?? _config.WorkDir,
                timeoutSeconds ?? 60,
                cancellationToken);

            await _sessionService.IncrementCommandCountAsync(sessionId, cancellationToken);
            return result;
        }
        finally
        {
            await _sessionService.SetExecutingCommandAsync(sessionId, false, cancellationToken);
        }
    }

    /// <summary>
    /// 流式执行命令
    /// </summary>
    public async IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(
        string sessionId, 
        string command, 
        string? workingDirectory = null, 
        int? timeoutSeconds = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        
        await _sessionService.SetExecutingCommandAsync(sessionId, true, cancellationToken);
        try
        {
            await foreach (var outputEvent in _dockerService.ExecuteCommandStreamAsync(
                session.ContainerId!,
                command,
                workingDirectory ?? _config.WorkDir,
                timeoutSeconds ?? 60,
                cancellationToken))
            {
                yield return outputEvent;
            }

            await _sessionService.IncrementCommandCountAsync(sessionId, cancellationToken);
        }
        finally
        {
            await _sessionService.SetExecutingCommandAsync(sessionId, false, cancellationToken);
        }
    }

    /// <summary>
    /// 流式执行命令数组（直接执行，不经过shell包装）
    /// </summary>
    public async IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(
        string sessionId, 
        string[] command, 
        string? workingDirectory = null, 
        int? timeoutSeconds = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        
        await _sessionService.SetExecutingCommandAsync(sessionId, true, cancellationToken);
        try
        {
            await foreach (var outputEvent in _dockerService.ExecuteCommandStreamAsync(
                session.ContainerId!,
                command,
                workingDirectory ?? _config.WorkDir,
                timeoutSeconds ?? 60,
                cancellationToken))
            {
                yield return outputEvent;
            }

            await _sessionService.IncrementCommandCountAsync(sessionId, cancellationToken);
        }
        finally
        {
            await _sessionService.SetExecutingCommandAsync(sessionId, false, cancellationToken);
        }
    }

    #endregion

    #region File Operations

    /// <summary>
    /// 上传文件到会话容器
    /// </summary>
    public async Task UploadFileAsync(string sessionId, string targetPath, byte[] content, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        await _dockerService.UploadFileAsync(session.ContainerId!, targetPath, content, cancellationToken);
        await _sessionService.UpdateSessionActivityAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// 列出目录
    /// </summary>
    public async Task<List<FileEntry>> ListDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        var result = await _dockerService.ListDirectoryAsync(session.ContainerId!, path, cancellationToken);
        await _sessionService.UpdateSessionActivityAsync(sessionId, cancellationToken);
        return result;
    }

    /// <summary>
    /// 下载文件
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(string sessionId, string filePath, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        var result = await _dockerService.DownloadFileAsync(session.ContainerId!, filePath, cancellationToken);
        await _sessionService.UpdateSessionActivityAsync(sessionId, cancellationToken);
        return result;
    }

    /// <summary>
    /// 删除文件
    /// </summary>
    public async Task DeleteFileAsync(string sessionId, string filePath, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        var result = await _dockerService.ExecuteCommandAsync(session.ContainerId!, $"rm -f \"{filePath}\"", "/", 10, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to delete file: {result.Stderr}");
        }
        await _sessionService.UpdateSessionActivityAsync(sessionId, cancellationToken);
    }

    #endregion

    #region Container Operations (Admin)

    /// <summary>
    /// 获取所有容器
    /// </summary>
    public async Task<List<ContainerInfo>> GetAllContainersAsync(CancellationToken cancellationToken = default)
    {
        return await _poolService.GetAllContainersAsync(cancellationToken);
    }

    /// <summary>
    /// 创建预热容器
    /// </summary>
    public async Task<ContainerInfo> CreateContainerAsync(CancellationToken cancellationToken = default)
    {
        return await _poolService.CreateContainerAsync(cancellationToken);
    }

    /// <summary>
    /// 删除容器
    /// </summary>
    public async Task DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await _sessionService.OnContainerDeletedAsync(containerId, cancellationToken);
        await _poolService.ForceDeleteContainerAsync(containerId, cancellationToken);
    }

    /// <summary>
    /// 删除所有容器
    /// </summary>
    public async Task DeleteAllContainersAsync(CancellationToken cancellationToken = default)
    {
        await _poolService.DeleteAllContainersAsync(cancellationToken);
    }

    /// <summary>
    /// 触发预热
    /// </summary>
    public async Task TriggerPrewarmAsync(CancellationToken cancellationToken = default)
    {
        await _poolService.EnsurePrewarmAsync(cancellationToken);
    }

    #endregion

    #region Usage and Metrics

    /// <summary>
    /// 获取会话使用量统计
    /// </summary>
    public async Task<SessionUsage?> GetSessionUsageAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken);
        var usage = await _dockerService.GetContainerStatsAsync(session.ContainerId!, cancellationToken);
        
        if (usage != null)
        {
            usage.SessionId = sessionId;
            usage.CommandCount = session.CommandCount;
            usage.CreatedAt = session.CreatedAt;
        }
        
        return usage;
    }

    /// <summary>
    /// 获取 Artifacts 目录中的文件列表
    /// </summary>
    public async Task<List<FileEntry>> GetArtifactsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var artifactsPath = $"{_config.WorkDir}/{_config.ArtifactsDir}";
        try
        {
            return await ListDirectoryAsync(sessionId, artifactsPath, cancellationToken);
        }
        catch
        {
            // Artifacts 目录可能不存在
            return new List<FileEntry>();
        }
    }

    /// <summary>
    /// 下载 Artifact 文件
    /// </summary>
    public async Task<byte[]> DownloadArtifactAsync(string sessionId, string fileName, CancellationToken cancellationToken = default)
    {
        var artifactPath = $"{_config.WorkDir}/{_config.ArtifactsDir}/{fileName}";
        return await DownloadFileAsync(sessionId, artifactPath, cancellationToken);
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// 清理超时会话
    /// </summary>
    public async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        await _cleanupService.CleanupExpiredSessionsAsync(cancellationToken);
    }

    #endregion

    private async Task<SessionInfo> GetActiveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessionService.GetSessionAsync(sessionId, cancellationToken);
        if (session == null)
        {
            throw new SessionNotFoundException(sessionId);
        }
        if (string.IsNullOrEmpty(session.ContainerId))
        {
            throw new SessionNotReadyException(sessionId);
        }
        return session;
    }

    public void Dispose()
    {
        _dockerService.Dispose();
        GC.SuppressFinalize(this);
    }
}
