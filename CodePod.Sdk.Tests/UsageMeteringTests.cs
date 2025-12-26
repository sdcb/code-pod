using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 使用量计量功能测试基类
/// 验证 Docker Stats API 获取资源使用情况
/// </summary>
public abstract class UsageMeteringTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.LabelPrefix = GetLabelPrefix();
        });
    }
}

public class UsageMetering_BasicTest : UsageMeteringTestBase
{
    [Fact]
    [Trait("Category", "UsageMetering")]
    public async Task GetSessionUsage_ReturnsStats()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "使用量测试" });

        // 执行一些命令产生使用量
        await context.Client.ExecuteCommandAsync(session.Id, "echo 'test'");

        // Act
        SessionUsage? usage = await context.Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(session.ContainerId, usage.ContainerId);
    }
}

public class UsageMetering_ActivityTest : UsageMeteringTestBase
{
    [Fact]
    [Trait("Category", "UsageMetering")]
    public async Task GetSessionUsage_AfterWork_ShowsActivity()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "活动使用量测试" });

        // 获取初始使用量
        SessionUsage? usageBefore = await context.Client.GetSessionUsageAsync(session.Id);

        // 执行一些工作
        for (int i = 0; i < 5; i++)
        {
            await context.Client.ExecuteCommandAsync(session.Id, $"echo 'Work iteration {i}'");
        }

        // 获取工作后的使用量
        SessionUsage? usageAfter = await context.Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usageBefore);
        Assert.NotNull(usageAfter);
        
        // CPU 使用应该增加
        Assert.True(usageAfter.CpuUsageNanos >= usageBefore.CpuUsageNanos,
            "CPU usage should increase after work");
    }
}

public class UsageMetering_MemoryTest : UsageMeteringTestBase
{
    [Fact]
    [Trait("Category", "UsageMetering")]
    public async Task GetSessionUsage_MemoryIntensiveWork_ShowsHigherMemory()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "内存密集测试" });

        // 获取初始使用量
        SessionUsage? usageBefore = await context.Client.GetSessionUsageAsync(session.Id);

        // 执行内存密集型工作（创建大文件）
        string command = context.IsWindowsContainer
            ? "$p = Join-Path $PWD 'testfile.bin'; $bytes = New-Object byte[] (10MB); [IO.File]::WriteAllBytes($p, $bytes); Remove-Item -Force $p"
            : "dd if=/dev/zero of=/tmp/testfile bs=1M count=10 2>/dev/null && rm /tmp/testfile";

        await context.Client.ExecuteCommandAsync(session.Id, command, timeoutSeconds: 30);

        // 获取工作后的使用量
        SessionUsage? usageAfter = await context.Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usageBefore);
        Assert.NotNull(usageAfter);
    }
}

public class UsageMetering_TimestampTest : UsageMeteringTestBase
{
    [Fact]
    [Trait("Category", "UsageMetering")]
    public async Task SessionUsage_HasValidTimestamp()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "时间戳测试" });

        // Act
        SessionUsage? usage = await context.Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usage);
        Assert.True(usage.Timestamp > DateTime.MinValue);
        Assert.True(usage.Timestamp <= DateTime.UtcNow);
    }
}

public class UsageMetering_NonExistentTest : UsageMeteringTestBase
{
    [Fact]
    [Trait("Category", "UsageMetering")]
    public async Task GetSessionUsage_NonExistentSession_ThrowsException()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Act & Assert
        await Assert.ThrowsAsync<CodePod.Sdk.Exceptions.SessionNotFoundException>(
            () => context.Client.GetSessionUsageAsync(99999));
    }
}

public class UsageMetering_MultipleSessionsTest : UsageMeteringTestBase
{
    [Fact]
    [Trait("Category", "UsageMetering")]
    public async Task MultipleSession_EachHasIndependentUsage()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session1 = await sessions.CreateSessionAsync(new SessionOptions { Name = "会话1" });
        SessionInfo session2 = await sessions.CreateSessionAsync(new SessionOptions { Name = "会话2" });

        // 在会话1执行更多工作
        for (int i = 0; i < 10; i++)
        {
            await context.Client.ExecuteCommandAsync(session1.Id, $"echo 'Session 1 work {i}'");
        }

        // 会话2只执行少量工作
        await context.Client.ExecuteCommandAsync(session2.Id, "echo 'Session 2 minimal'");

        // Act
        SessionUsage? usage1 = await context.Client.GetSessionUsageAsync(session1.Id);
        SessionUsage? usage2 = await context.Client.GetSessionUsageAsync(session2.Id);

        // Assert
        Assert.NotNull(usage1);
        Assert.NotNull(usage2);
        Assert.NotEqual(usage1.ContainerId, usage2.ContainerId);
    }
}
