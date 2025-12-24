using CodePod.Sdk.Configuration;
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

    public virtual async Task InitializeAsync()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var settings = TestSettings.Load();
        var isWindowsContainer = settings.IsWindowsContainer;

        Config = new CodePodConfig
        {
            IsWindowsContainer = isWindowsContainer,
            DockerEndpoint = settings.DockerEndpoint,
            Image = isWindowsContainer ? settings.DotnetSdkWindowsImage : settings.DotnetSdkLinuxImage,
            PrewarmCount = 2,
            MaxContainers = 10,
            SessionTimeoutSeconds = 1800,
            // Windows 容器使用 Windows 路径
            WorkDir = isWindowsContainer ? "C:\\app" : "/app",
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
            // 先停止后台任务，防止在清理期间继续创建容器
            Client.Dispose();
            
            // 等待一小段时间，确保后台任务已经停止
            await Task.Delay(100);
            
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

            // 清理所有容器，使用 CancellationToken.None 确保清理一定完成
            await Client.DeleteAllContainersAsync(CancellationToken.None);
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
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
    /// 当前容器工作目录（来自配置）
    /// </summary>
    protected string WorkDir => Config.WorkDir;

    /// <summary>
    /// 在 WorkDir 下构造文件/目录路径（不依赖宿主机 OS）
    /// </summary>
    protected string GetWorkPath(string relativePath)
    {
        var rel = relativePath.TrimStart('/', '\\');

        if (IsWindowsContainer)
        {
            rel = rel.Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(rel))
            {
                return WorkDir;
            }

            return WorkDir.EndsWith("\\", StringComparison.Ordinal)
                ? WorkDir + rel
                : WorkDir + "\\" + rel;
        }

        rel = rel.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(rel))
        {
            return WorkDir;
        }

        return WorkDir.EndsWith("/", StringComparison.Ordinal)
            ? WorkDir + rel
            : WorkDir + "/" + rel;
    }

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
