using System.Runtime.CompilerServices;
using CodePod.Sdk.Configuration;
using CodePod.Sdk.Storage;
using Microsoft.Extensions.Options;

// 类型别名用于区分 SDK 和 Web 模型
using SdkSessionInfo = CodePod.Sdk.Models.SessionInfo;
using SdkSessionStatus = CodePod.Sdk.Models.SessionStatus;
using SdkContainerInfo = CodePod.Sdk.Models.ContainerInfo;
using SdkContainerStatus = CodePod.Sdk.Models.ContainerStatus;
using SdkCommandResult = CodePod.Sdk.Models.CommandResult;
using SdkCommandOutputEvent = CodePod.Sdk.Models.CommandOutputEvent;
using SdkCommandOutputType = CodePod.Sdk.Models.CommandOutputType;
using SdkFileEntry = CodePod.Sdk.Models.FileEntry;

using WebSessionInfo = DockerShellHost.Models.SessionInfo;
using WebSessionStatus = DockerShellHost.Models.SessionStatus;
using WebContainerInfo = DockerShellHost.Models.ContainerInfo;
using WebContainerStatus = DockerShellHost.Models.ContainerStatus;
using WebCommandResult = DockerShellHost.Models.CommandResult;
using WebCommandOutputEvent = DockerShellHost.Models.CommandOutputEvent;
using WebCommandOutputType = DockerShellHost.Models.CommandOutputType;
using WebFileEntry = DockerShellHost.Models.FileEntry;
using SdkDockerService = CodePod.Sdk.Services.DockerService;
using SdkDockerPoolService = CodePod.Sdk.Services.DockerPoolService;
using SdkSessionService = CodePod.Sdk.Services.SessionService;

namespace DockerShellHost.Services;

/// <summary>
/// 模型转换器 - SDK 模型到 Web 模型
/// </summary>
public static class ModelConverter
{
    public static WebSessionInfo ToWeb(this SdkSessionInfo sdk) => new WebSessionInfo
    {
        SessionId = sdk.SessionId,
        Name = sdk.Name,
        ContainerId = sdk.ContainerId,
        Status = (WebSessionStatus)(int)sdk.Status,
        QueuePosition = sdk.QueuePosition,
        CreatedAt = sdk.CreatedAt,
        LastActivityAt = sdk.LastActivityAt,
        CommandCount = sdk.CommandCount,
        IsExecutingCommand = sdk.IsExecutingCommand,
        TimeoutSeconds = sdk.TimeoutSeconds
    };

    public static WebContainerInfo ToWeb(this SdkContainerInfo sdk) => new WebContainerInfo
    {
        ContainerId = sdk.ContainerId,
        Name = sdk.Name,
        Image = sdk.Image,
        DockerStatus = sdk.DockerStatus,
        Status = (WebContainerStatus)(int)sdk.Status,
        CreatedAt = sdk.CreatedAt,
        StartedAt = sdk.StartedAt,
        SessionId = sdk.SessionId,
        Labels = sdk.Labels
    };

    public static WebCommandResult ToWeb(this SdkCommandResult sdk) => new WebCommandResult
    {
        Stdout = sdk.Stdout,
        Stderr = sdk.Stderr,
        ExitCode = sdk.ExitCode,
        ExecutionTimeMs = sdk.ExecutionTimeMs
    };

    public static WebCommandOutputEvent ToWeb(this SdkCommandOutputEvent sdk) => new WebCommandOutputEvent
    {
        Type = (WebCommandOutputType)(int)sdk.Type,
        Data = sdk.Data,
        ExitCode = sdk.ExitCode,
        ExecutionTimeMs = sdk.ExecutionTimeMs
    };

    public static WebFileEntry ToWeb(this SdkFileEntry sdk) => new WebFileEntry
    {
        Name = sdk.Name,
        Path = sdk.Path,
        IsDirectory = sdk.IsDirectory,
        Size = sdk.Size,
        LastModified = sdk.LastModified
    };
}

/// <summary>
/// Docker 服务适配器 - 将 SDK DockerService 适配到 Web IDockerService 接口
/// </summary>
public class DockerServiceAdapter : IDockerService
{
    private readonly SdkDockerService _sdkService;

    public DockerServiceAdapter(SdkDockerService sdkService)
    {
        _sdkService = sdkService;
    }

    public async Task EnsureImageAsync(CancellationToken cancellationToken = default)
    {
        await _sdkService.EnsureImageAsync(cancellationToken);
    }

    public async Task<WebContainerInfo> CreateContainerAsync(string? sessionId = null, bool isWarm = false, CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.CreateContainerAsync(sessionId, isWarm, cancellationToken);
        return result.ToWeb();
    }

    public async Task<List<WebContainerInfo>> GetManagedContainersAsync(CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.GetManagedContainersAsync(cancellationToken);
        return result.Select(c => c.ToWeb()).ToList();
    }

    public async Task<WebContainerInfo?> GetContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.GetContainerAsync(containerId, cancellationToken);
        return result?.ToWeb();
    }

    public async Task DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await _sdkService.DeleteContainerAsync(containerId, cancellationToken);
    }

    public async Task DeleteAllManagedContainersAsync(CancellationToken cancellationToken = default)
    {
        await _sdkService.DeleteAllManagedContainersAsync(cancellationToken);
    }

    public async Task<WebCommandResult> ExecuteCommandAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.ExecuteCommandAsync(containerId, command, workingDirectory, timeoutSeconds, cancellationToken);
        return result.ToWeb();
    }

    public async IAsyncEnumerable<WebCommandOutputEvent> ExecuteCommandStreamAsync(
        string containerId, 
        string command, 
        string workingDirectory, 
        int timeoutSeconds, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _sdkService.ExecuteCommandStreamAsync(containerId, command, workingDirectory, timeoutSeconds, cancellationToken))
        {
            yield return evt.ToWeb();
        }
    }

    public async Task UploadFileAsync(string containerId, string containerPath, byte[] content, CancellationToken cancellationToken = default)
    {
        await _sdkService.UploadFileAsync(containerId, containerPath, content, cancellationToken);
    }

    public async Task<List<WebFileEntry>> ListDirectoryAsync(string containerId, string path, CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.ListDirectoryAsync(containerId, path, cancellationToken);
        return result.Select(f => f.ToWeb()).ToList();
    }

    public async Task<byte[]> DownloadFileAsync(string containerId, string filePath, CancellationToken cancellationToken = default)
    {
        return await _sdkService.DownloadFileAsync(containerId, filePath, cancellationToken);
    }

    public async Task AssignSessionToContainerAsync(string containerId, string sessionId, CancellationToken cancellationToken = default)
    {
        await _sdkService.AssignSessionToContainerAsync(containerId, sessionId, cancellationToken);
    }

    public void Dispose()
    {
        _sdkService.Dispose();
    }
}

/// <summary>
/// Docker Pool 服务适配器
/// </summary>
public class DockerPoolServiceAdapter : IDockerPoolService
{
    private readonly SdkDockerPoolService _sdkService;
    private readonly SdkDockerService _sdkDockerService;

    public event EventHandler? OnStatusChanged;

    public DockerPoolServiceAdapter(
        SdkDockerPoolService sdkService,
        SdkDockerService sdkDockerService)
    {
        _sdkService = sdkService;
        _sdkDockerService = sdkDockerService;
        
        // 转发事件
        _sdkService.OnStatusChanged += (s, e) => OnStatusChanged?.Invoke(this, e);
    }

    public async Task<WebContainerInfo?> AcquireContainerAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.AcquireContainerAsync(sessionId, cancellationToken);
        return result?.ToWeb();
    }

    public async Task ReleaseContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await _sdkService.ReleaseContainerAsync(containerId, cancellationToken);
    }

    public async Task EnsurePrewarmAsync(CancellationToken cancellationToken = default)
    {
        await _sdkService.EnsurePrewarmAsync(cancellationToken);
    }

    public async Task DeleteAllContainersAsync(CancellationToken cancellationToken = default)
    {
        await _sdkService.DeleteAllContainersAsync(cancellationToken);
    }

    public async Task<WebContainerInfo> CreateContainerAsync(CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.CreateContainerAsync(cancellationToken);
        return result.ToWeb();
    }

    public async Task ForceDeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await _sdkService.ForceDeleteContainerAsync(containerId, cancellationToken);
    }

    public async Task<List<WebContainerInfo>> GetAllContainersAsync(CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.GetAllContainersAsync(cancellationToken);
        return result.Select(c => c.ToWeb()).ToList();
    }
}

/// <summary>
/// Session 服务适配器
/// </summary>
public class SessionServiceAdapter : ISessionService
{
    private readonly SdkSessionService _sdkService;

    public SessionServiceAdapter(SdkSessionService sdkService)
    {
        _sdkService = sdkService;
    }

    public int MaxTimeoutSeconds => _sdkService.MaxTimeoutSeconds;

    public async Task<WebSessionInfo> CreateSessionAsync(string? name = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
    {
        var result = await _sdkService.CreateSessionAsync(name, timeoutSeconds, cancellationToken);
        return result.ToWeb();
    }

    public IEnumerable<WebSessionInfo> GetAllSessions()
    {
        // 同步包装异步方法
        var result = _sdkService.GetAllSessionsAsync(CancellationToken.None).GetAwaiter().GetResult();
        return result.Select(s => s.ToWeb());
    }

    public WebSessionInfo? GetSession(string sessionId)
    {
        // 同步包装异步方法
        var result = _sdkService.GetSessionAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult();
        return result?.ToWeb();
    }

    public async Task DestroySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _sdkService.DestroySessionAsync(sessionId, cancellationToken);
    }

    public void UpdateSessionActivity(string sessionId)
    {
        // 同步包装异步方法
        _sdkService.UpdateSessionActivityAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public int GetQueuedCount()
    {
        // 同步包装异步方法
        return _sdkService.GetQueuedCountAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void OnContainerDeleted(string containerId)
    {
        // 同步包装异步方法
        _sdkService.OnContainerDeletedAsync(containerId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void IncrementCommandCount(string sessionId)
    {
        // 同步包装异步方法
        _sdkService.IncrementCommandCountAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void SetExecutingCommand(string sessionId, bool isExecuting)
    {
        // 同步包装异步方法
        _sdkService.SetExecutingCommandAsync(sessionId, isExecuting, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>
/// 配置适配器 - 将 Web 配置转换为 SDK 配置
/// </summary>
public static class ConfigurationAdapter
{
    public static CodePodConfig ToSdkConfig(Configuration.DockerPoolConfig webConfig)
    {
        return new CodePodConfig
        {
            Image = webConfig.Image,
            PrewarmCount = webConfig.PrewarmCount,
            MaxContainers = webConfig.MaxContainers,
            SessionTimeoutSeconds = webConfig.SessionTimeoutSeconds,
            WorkDir = webConfig.WorkDir,
            LabelPrefix = webConfig.LabelPrefix
        };
    }
}

/// <summary>
/// SDK 服务注册扩展
/// </summary>
public static class SdkServiceExtensions
{
    /// <summary>
    /// 添加 SDK 服务到 DI 容器
    /// </summary>
    public static IServiceCollection AddCodePodSdk(this IServiceCollection services, IConfiguration configuration)
    {
        // 读取配置
        var webConfig = configuration.GetSection("DockerPool").Get<Configuration.DockerPoolConfig>() 
            ?? new Configuration.DockerPoolConfig();
        var sdkConfig = ConfigurationAdapter.ToSdkConfig(webConfig);

        // 注册 SDK 核心组件
        services.AddSingleton(sdkConfig);
        services.AddSingleton<ISessionStorage, InMemorySessionStorage>();
        services.AddSingleton<IContainerStorage, InMemoryContainerStorage>();
        services.AddSingleton<CodePod.Sdk.Services.DockerClientFactory>();
        
        // 注册 SDK 服务（单例）
        services.AddSingleton<SdkDockerService>(sp =>
        {
            var clientFactory = sp.GetRequiredService<CodePod.Sdk.Services.DockerClientFactory>();
            var config = sp.GetRequiredService<CodePodConfig>();
            var logger = sp.GetRequiredService<ILogger<SdkDockerService>>();
            return new SdkDockerService(clientFactory, config, logger);
        });

        services.AddSingleton<SdkDockerPoolService>(sp =>
        {
            var dockerService = sp.GetRequiredService<SdkDockerService>();
            var containerStorage = sp.GetRequiredService<IContainerStorage>();
            var config = sp.GetRequiredService<CodePodConfig>();
            var logger = sp.GetRequiredService<ILogger<SdkDockerPoolService>>();
            return new SdkDockerPoolService(dockerService, containerStorage, config, logger);
        });

        services.AddSingleton<SdkSessionService>(sp =>
        {
            var sessionStorage = sp.GetRequiredService<ISessionStorage>();
            var poolService = sp.GetRequiredService<SdkDockerPoolService>();
            var config = sp.GetRequiredService<CodePodConfig>();
            var logger = sp.GetRequiredService<ILogger<SdkSessionService>>();
            return new SdkSessionService(sessionStorage, poolService, config, logger);
        });

        // 注册适配器（Web 接口）
        services.AddSingleton<IDockerService, DockerServiceAdapter>(sp =>
        {
            var sdkService = sp.GetRequiredService<SdkDockerService>();
            return new DockerServiceAdapter(sdkService);
        });

        services.AddSingleton<IDockerPoolService, DockerPoolServiceAdapter>(sp =>
        {
            var sdkService = sp.GetRequiredService<SdkDockerPoolService>();
            var sdkDockerService = sp.GetRequiredService<SdkDockerService>();
            return new DockerPoolServiceAdapter(sdkService, sdkDockerService);
        });

        services.AddSingleton<ISessionService, SessionServiceAdapter>(sp =>
        {
            var sdkService = sp.GetRequiredService<SdkSessionService>();
            return new SessionServiceAdapter(sdkService);
        });

        return services;
    }
}
