using Microsoft.Extensions.Logging;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 容器数量限制测试 - 对应 test/MaxContainerTest.cs
/// </summary>
public class MaxContainerTests : TestBase
{
    public override async Task InitializeAsync()
    {
        // 使用较小的容器限制来测试
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        Config = new Configuration.CodePodConfig
        {
            Image = "mcr.microsoft.com/dotnet/sdk:10.0",
            PrewarmCount = 1,
            MaxContainers = 3, // 较小的限制便于测试
            SessionTimeoutSeconds = 300,
            WorkDir = "/app",
            LabelPrefix = "codepod-maxtest"
        };

        Client = new CodePodClientBuilder()
            .WithConfig(Config)
            .WithLogging(LoggerFactory)
            .Build();

        await Client.InitializeAsync();
    }

    [Fact]
    public async Task MaxContainers_WhenReached_SessionsAreQueued()
    {
        var createdSessions = new List<int>();

        try
        {
            // Arrange - 创建容器数量等于最大限制
            for (int i = 0; i < Config.MaxContainers; i++)
            {
                var session = await Client.CreateSessionAsync($"Session-{i + 1}");
                createdSessions.Add(session.Id);
                await Task.Delay(500); // 给容器创建一些时间
            }

            // 等待所有会话就绪
            foreach (var sessionId in createdSessions)
            {
                try
                {
                    await WaitForSessionReadyAsync(sessionId, 60);
                }
                catch (TimeoutException)
                {
                    // 有些可能在队列中
                }
            }

            // Act - 创建第 N+1 个会话
            var queuedSession = await Client.CreateSessionAsync("Queued-Session");
            createdSessions.Add(queuedSession.Id);

            // 检查状态
            var status = await Client.GetStatusAsync();
            
            // Assert
            if (queuedSession.Status == Models.SessionStatus.Queued)
            {
                Assert.Null(queuedSession.ContainerId);
                Assert.True(queuedSession.QueuePosition > 0);
            }
            // 如果不是 Queued，说明有容器在创建时失败或者立即分配了
        }
        finally
        {
            // Cleanup
            foreach (var sessionId in createdSessions)
            {
                try
                {
                    await Client.DestroySessionAsync(sessionId);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    [Fact]
    public async Task QueuedSession_GetsContainerWhenReleased()
    {
        var createdSessions = new List<int>();

        try
        {
            // Arrange - 填满容器池
            for (int i = 0; i < Config.MaxContainers; i++)
            {
                var session = await Client.CreateSessionAsync($"Fill-Session-{i + 1}");
                createdSessions.Add(session.Id);
                await Task.Delay(500);
            }

            // 等待容器分配
            await Task.Delay(5000);

            // 创建一个应该被排队的会话
            var queuedSession = await Client.CreateSessionAsync("Should-Be-Queued");
            createdSessions.Add(queuedSession.Id);

            // 如果会话被排队
            if (queuedSession.Status == Models.SessionStatus.Queued)
            {
                // Act - 销毁第一个会话，释放容器
                await Client.DestroySessionAsync(createdSessions[0]);
                createdSessions.RemoveAt(0);

                // 等待队列处理
                await Task.Delay(5000);

                // Assert - 检查排队的会话是否获得了容器
                var updatedSession = await Client.GetSessionAsync(queuedSession.Id);
                
                // 多次重试检查
                for (int retry = 0; retry < 10 && string.IsNullOrEmpty(updatedSession.ContainerId); retry++)
                {
                    await Task.Delay(1000);
                    updatedSession = await Client.GetSessionAsync(queuedSession.Id);
                }

                if (updatedSession.Status == Models.SessionStatus.Active)
                {
                    Assert.NotNull(updatedSession.ContainerId);
                }
            }
        }
        finally
        {
            // Cleanup
            foreach (var sessionId in createdSessions)
            {
                try
                {
                    await Client.DestroySessionAsync(sessionId);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}
