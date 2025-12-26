using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 容器数量限制测试基类
/// </summary>
public abstract class MaxContainerTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.PrewarmCount = 0;
            config.MaxContainers = 3;
            config.LabelPrefix = GetLabelPrefix();
        });
    }
}

public class MaxContainers_QueuedTest : MaxContainerTestBase
{
    [Fact]
    [Trait("Category", "MaxContainers")]
    public async Task MaxContainers_WhenReached_SessionsAreQueued()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange - 创建容器数量等于最大限制
        for (int i = 0; i < context.Config.MaxContainers; i++)
        {
            await sessions.CreateSessionAsync(new SessionOptions { Name = $"Session-{i + 1}" });
        }

        // Act & Assert - 达到上限后应直接失败
        await Assert.ThrowsAsync<CodePod.Sdk.Exceptions.MaxContainersReachedException>(() =>
            sessions.CreateSessionAsync(new SessionOptions { Name = "Exceed-Max" }));
    }
}

public class MaxContainers_ReleaseTest : MaxContainerTestBase
{
    [Fact]
    [Trait("Category", "MaxContainers")]
    public async Task QueuedSession_GetsContainerWhenReleased()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);
        List<int> createdSessions = new();

        // Arrange - 填满容器池
        for (int i = 0; i < context.Config.MaxContainers; i++)
        {
            SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = $"Fill-Session-{i + 1}" });
            createdSessions.Add(session.Id);
        }

        // Act - 先释放一个容器，然后再次创建应成功
        await context.Client.DestroySessionAsync(createdSessions[0]);
        await Task.Delay(500); // 给清理一些时间

        SessionInfo sessionAfterRelease = await sessions.CreateSessionAsync(new SessionOptions { Name = "After-Release" });

        // Assert
        Assert.False(string.IsNullOrEmpty(sessionAfterRelease.ContainerId));
    }
}
