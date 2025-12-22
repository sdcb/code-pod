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

        Config = new CodePodConfig
        {
            Image = "mcr.microsoft.com/dotnet/sdk:10.0",
            PrewarmCount = 2,
            MaxContainers = 10,
            SessionTimeoutSeconds = 1800,
            WorkDir = "/app",
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
                    await Client.DestroySessionAsync(session.SessionId);
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
    protected async Task<Models.SessionInfo> WaitForSessionReadyAsync(string sessionId, int maxWaitSeconds = 30)
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
}
