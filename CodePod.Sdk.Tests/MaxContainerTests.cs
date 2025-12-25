using CodePod.Sdk.Models;
using Microsoft.Extensions.Logging;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 容器数量限制测试 - 对应 test/MaxContainerTest.cs
/// </summary>
public class MaxContainerTests : TestBase
{
    public override async Task InitializeAsync()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        CodePodTestSettings settings = TestSettings.Load();
        bool isWindowsContainer = settings.IsWindowsContainer;

        Config = new Configuration.CodePodConfig
        {
            IsWindowsContainer = isWindowsContainer,
            DockerEndpoint = settings.DockerEndpoint,
            Image = isWindowsContainer ? settings.DotnetSdkWindowsImage : settings.DotnetSdkLinuxImage,
            PrewarmCount = 0,
            MaxContainers = 3,
            SessionTimeoutSeconds = 300,
            WorkDir = isWindowsContainer ? "C:\\app" : "/app",
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
        await using TestSessionTracker sessions = new(Client);
        // Arrange - 创建容器数量等于最大限制
        for (int i = 0; i < Config.MaxContainers; i++)
        {
            await sessions.CreateSessionAsync(new SessionOptions { Name = $"Session-{i + 1}" });
        }

        // Act & Assert - 达到上限后应直接失败（不会返回 queued session）
        await Assert.ThrowsAsync<CodePod.Sdk.Exceptions.MaxContainersReachedException>(() =>
            sessions.CreateSessionAsync(new SessionOptions { Name = "Exceed-Max" }));
    }

    [Fact]
    public async Task QueuedSession_GetsContainerWhenReleased()
    {
        await using TestSessionTracker sessions = new(Client);
        List<int> createdSessions = new();

        // Arrange - 填满容器池
        for (int i = 0; i < Config.MaxContainers; i++)
        {
            SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = $"Fill-Session-{i + 1}" });
            createdSessions.Add(session.Id);
        }

        // Act - 先释放一个容器，然后再次创建应成功
        await Client.DestroySessionAsync(createdSessions[0]);
        await Task.Delay(500); // 给清理一些时间

        SessionInfo sessionAfterRelease = await sessions.CreateSessionAsync(new SessionOptions { Name = "After-Release" });

        // Assert
        Assert.False(string.IsNullOrEmpty(sessionAfterRelease.ContainerId));
    }
}
