using System.Runtime.InteropServices;
using CodePod.Sdk.Configuration;
using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 测试基类，提供 CodePodClient 实例和清理逻辑
/// </summary>
public abstract class TestBase : IAsyncLifetime
{
    protected CodePodClient Client { get; set; } = null!;
    protected CodePodConfig Config { get; set; } = null!;
    protected ILoggerFactory LoggerFactory { get; set; } = null!;

    /// <summary>
    /// Docker 服务器信息
    /// </summary>
    protected record DockerServerInfo(bool IsWindows, Version? KernelVersion, string Endpoint);

    /// <summary>
    /// 远程 Windows Docker 端点（Windows Server 2022 LTSC）
    /// 设置为 null 则使用本地 Docker
    /// </summary>
    protected const string? RemoteWindowsDockerEndpoint = "tcp://192.168.3.97:2375";

    /// <summary>
    /// 获取 Docker 端点 URI
    /// </summary>
    protected static Uri GetDockerEndpointUri()
    {
        if (!string.IsNullOrEmpty(RemoteWindowsDockerEndpoint))
        {
            return new Uri(RemoteWindowsDockerEndpoint);
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");
    }

    /// <summary>
    /// 检测 Docker 服务器环境
    /// </summary>
    protected static async Task<DockerServerInfo> GetDockerServerInfoAsync()
    {
        var endpoint = GetDockerEndpointUri();
        try
        {
            using var client = new DockerClientConfiguration(endpoint).CreateClient();
            var version = await client.System.GetVersionAsync();
            var isWindows = version.Os?.Equals("windows", StringComparison.OrdinalIgnoreCase) == true;
            
            // 解析内核版本（Windows: 10.0.xxxxx，其中 xxxxx 表示构建号）
            Version? kernelVersion = null;
            if (!string.IsNullOrEmpty(version.KernelVersion) && Version.TryParse(version.KernelVersion, out var kv))
            {
                kernelVersion = kv;
            }

            return new DockerServerInfo(isWindows, kernelVersion, endpoint.ToString());
        }
        catch
        {
            return new DockerServerInfo(false, null, endpoint.ToString());
        }
    }

    /// <summary>
    /// 根据 Docker 服务器版本选择合适的镜像
    /// Windows Server 2022+ (Build >= 20000): mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022
    /// Windows 10/Server 2019 (Build < 20000): mcr.microsoft.com/dotnet/sdk:9.0-windowsservercore-ltsc2019
    /// Linux: mcr.microsoft.com/dotnet/sdk:10.0
    /// </summary>
    protected static string GetDockerImage(DockerServerInfo serverInfo)
    {
        if (!serverInfo.IsWindows)
        {
            return "mcr.microsoft.com/dotnet/sdk:10.0";
        }

        // 使用远程 Windows Server 2022 时，固定使用 LTSC2022 镜像
        if (!string.IsNullOrEmpty(RemoteWindowsDockerEndpoint))
        {
            return "mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022";
        }

        // Windows 内核版本判断：
        // Windows 10/Server 2019: 10.0.17763 - 10.0.19045
        // Windows 11/Server 2022: 10.0.20000+
        // Windows Server 2025: 10.0.26000+
        if (serverInfo.KernelVersion != null && serverInfo.KernelVersion.Build >= 20000)
        {
            // Server 2022/2025 使用 LTSC2022 镜像
            return "mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022";
        }

        // Windows 10/Server 2019 使用 LTSC2019 镜像
        return "mcr.microsoft.com/dotnet/sdk:9.0-windowsservercore-ltsc2019";
    }

    public virtual async Task InitializeAsync()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // 自动检测 Docker 环境
        var serverInfo = await GetDockerServerInfoAsync();

        Config = new CodePodConfig
        {
            DockerEnvironment = serverInfo.IsWindows ? DockerEnvironment.Windows : DockerEnvironment.Linux,
            DockerEndpoint = serverInfo.Endpoint,
            Image = GetDockerImage(serverInfo),
            PrewarmCount = 2,
            MaxContainers = 10,
            SessionTimeoutSeconds = 1800,
            // Windows 容器使用 Windows 路径
            WorkDir = serverInfo.IsWindows ? "C:\\app" : "/app",
            LabelPrefix = "codepod-test"
        };

        Client = new CodePodClientBuilder()
            .WithConfig(Config)
            .WithLogging(LoggerFactory)
            .Build();

        await Client.InitializeAsync();
    }

    public virtual async Task DisposeAsync()
    {
        try
        {
            // 清理所有会话
            var sessions = await Client.GetAllSessionsAsync();
            foreach (var session in sessions)
            {
                try
                {
                    await Client.DestroySessionAsync(session.Id);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            // 清理所有容器
            await Client.DeleteAllContainersAsync();
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            Client.Dispose();
            LoggerFactory.Dispose();
        }
    }

    /// <summary>
    /// 等待会话就绪（获取到容器）
    /// </summary>
    protected async Task<Models.SessionInfo> WaitForSessionReadyAsync(int sessionId, int maxWaitSeconds = 30)
    {
        for (int i = 0; i < maxWaitSeconds * 2; i++)
        {
            var session = await Client.GetSessionAsync(sessionId);
            if (!string.IsNullOrEmpty(session.ContainerId))
            {
                return session;
            }
            await Task.Delay(500);
        }

        throw new TimeoutException($"Session {sessionId} did not become ready within {maxWaitSeconds} seconds");
    }

    /// <summary>
    /// 检查是否为 Windows 容器模式
    /// </summary>
    protected bool IsWindowsContainer => Config.IsWindowsContainer;

    /// <summary>
    /// 获取 echo 命令（跨平台）
    /// </summary>
    protected string GetEchoCommand(string message) =>
        IsWindowsContainer ? $"Write-Output '{message}'" : $"echo '{message}'";

    /// <summary>
    /// 获取打印多行的命令（跨平台）
    /// Linux: for i in 1 2 3; do echo "Line $i"; done
    /// Windows (pwsh): 1..3 | ForEach-Object { Write-Output "Line $_" }
    /// </summary>
    protected string GetMultiLineEchoCommand(int lineCount)
    {
        if (IsWindowsContainer)
        {
            // PowerShell: 使用 ForEach-Object
            return $"1..{lineCount} | ForEach-Object {{ Write-Output \"Line $_\" }}";
        }
        else
        {
            return $"for i in $(seq 1 {lineCount}); do echo \"Line $i\"; done";
        }
    }

    /// <summary>
    /// 获取带延迟的流式输出命令（跨平台）
    /// 用于测试流式输出
    /// </summary>
    protected string GetStreamingOutputCommand(int lineCount, double delaySeconds = 0.1)
    {
        if (IsWindowsContainer)
        {
            // PowerShell: 输出到 stdout 和 stderr
            var delayMs = (int)(delaySeconds * 1000);
            return $"1..{lineCount} | ForEach-Object {{ Write-Output \"stdout: Line $_\"; Write-Error \"stderr: Warning $_\"; Start-Sleep -Milliseconds {delayMs} }}";
        }
        else
        {
            return $"for i in $(seq 1 {lineCount}); do echo \"stdout: Line $i\"; echo \"stderr: Warning $i\" >&2; sleep {delaySeconds}; done";
        }
    }

    /// <summary>
    /// 获取创建目录命令（跨平台）
    /// </summary>
    protected string GetMkdirCommand(string path) =>
        Config.GetMkdirCommand(path);
}
