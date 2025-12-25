using CodePod.Sdk.Configuration;
using Microsoft.Extensions.Logging;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;
using CodePod.Sdk.Models;

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

        CodePodTestSettings settings = TestSettings.Load();
        Config = CodePodTestSupport.CreateDefaultConfig(settings);

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
            IReadOnlyList<SessionInfo> sessions = await Client.GetAllSessionsAsync();
            foreach (SessionInfo session in sessions)
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
        => await CodePodTestSupport.WaitForSessionReadyAsync(Client, sessionId, maxWaitSeconds);

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
        => CodePodTestSupport.GetWorkPath(Config, relativePath);

    /// <summary>
    /// 获取 echo 命令（跨平台）
    /// </summary>
    protected string GetEchoCommand(string message) =>
        CodePodTestSupport.GetEchoCommand(IsWindowsContainer, message);

    /// <summary>
    /// 获取打印多行的命令（跨平台）
    /// Linux: for i in 1 2 3; do echo "Line $i"; done
    /// Windows (pwsh): 1..3 | ForEach-Object { Write-Output "Line $_" }
    /// </summary>
    protected string GetMultiLineEchoCommand(int lineCount)
        => CodePodTestSupport.GetMultiLineEchoCommand(IsWindowsContainer, lineCount);

    /// <summary>
    /// 获取带延迟的流式输出命令（跨平台）
    /// 用于测试流式输出
    /// </summary>
    protected string GetStreamingOutputCommand(int lineCount, double delaySeconds = 0.1)
        => CodePodTestSupport.GetStreamingOutputCommand(IsWindowsContainer, lineCount, delaySeconds);

    /// <summary>
    /// 获取创建目录命令（跨平台）
    /// </summary>
    protected string GetMkdirCommand(string path) =>
        Config.GetMkdirCommand(path);
}
