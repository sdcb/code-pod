using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 使用量计量功能测试
/// 验证 Docker Stats API 获取资源使用情况
/// </summary>
[Collection(CodePodCollection.Name)]
public class UsageMeteringTests
{
    private readonly ITestOutputHelper _output;
    private readonly CodePodFixture _fixture;

    private CodePodClient Client => _fixture.Client;
    private bool IsWindowsContainer => _fixture.IsWindowsContainer;

    public UsageMeteringTests(CodePodFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task GetSessionUsage_ReturnsStats()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "使用量测试" });

        // 执行一些命令产生使用量
        await Client.ExecuteCommandAsync(session.Id, "echo 'test'");

        // Act
        SessionUsage? usage = await Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(session.ContainerId, usage.ContainerId);
        
        _output.WriteLine($"Container ID: {usage.ContainerId}");
        _output.WriteLine($"CPU Usage (nanos): {usage.CpuUsageNanos}");
        _output.WriteLine($"Memory Usage: {usage.MemoryUsageBytes / 1024 / 1024.0:F2} MB");
        _output.WriteLine($"Peak Memory: {usage.PeakMemoryBytes / 1024 / 1024.0:F2} MB");
        _output.WriteLine($"Network RX: {usage.NetworkRxBytes} bytes");
        _output.WriteLine($"Network TX: {usage.NetworkTxBytes} bytes");
    }

    [Fact]
    public async Task GetSessionUsage_AfterWork_ShowsActivity()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "活动使用量测试" });

        // 获取初始使用量
        SessionUsage? usageBefore = await Client.GetSessionUsageAsync(session.Id);

        // 执行一些工作
        for (int i = 0; i < 5; i++)
        {
            await Client.ExecuteCommandAsync(session.Id, $"echo 'Work iteration {i}'");
        }

        // 获取工作后的使用量
        SessionUsage? usageAfter = await Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usageBefore);
        Assert.NotNull(usageAfter);
        
        // CPU 使用应该增加
        Assert.True(usageAfter.CpuUsageNanos >= usageBefore.CpuUsageNanos,
            "CPU usage should increase after work");
        
        _output.WriteLine($"CPU before: {usageBefore.CpuUsageNanos}, after: {usageAfter.CpuUsageNanos}");
        _output.WriteLine($"Memory before: {usageBefore.MemoryUsageBytes}, after: {usageAfter.MemoryUsageBytes}");
    }

    [Fact]
    public async Task GetSessionUsage_MemoryIntensiveWork_ShowsHigherMemory()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "内存密集测试" });

        // 获取初始使用量
        SessionUsage? usageBefore = await Client.GetSessionUsageAsync(session.Id);

        // 执行内存密集型工作（创建大文件）
        string command = IsWindowsContainer
            ? "$p = Join-Path $PWD 'testfile.bin'; $bytes = New-Object byte[] (10MB); [IO.File]::WriteAllBytes($p, $bytes); Remove-Item -Force $p"
            : "dd if=/dev/zero of=/tmp/testfile bs=1M count=10 2>/dev/null && rm /tmp/testfile";

        await Client.ExecuteCommandAsync(session.Id, command, timeoutSeconds: 30);

        // 获取工作后的使用量
        SessionUsage? usageAfter = await Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usageBefore);
        Assert.NotNull(usageAfter);
        
        // 峰值内存应该有所变化
        _output.WriteLine($"Peak memory before: {usageBefore.PeakMemoryBytes / 1024.0:F2} KB");
        _output.WriteLine($"Peak memory after: {usageAfter.PeakMemoryBytes / 1024.0:F2} KB");
    }

    [Fact]
    public async Task SessionUsage_HasValidTimestamp()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "时间戳测试" });

        // Act
        SessionUsage? usage = await Client.GetSessionUsageAsync(session.Id);

        // Assert
        Assert.NotNull(usage);
        Assert.True(usage.Timestamp > DateTime.MinValue);
        Assert.True(usage.Timestamp <= DateTime.UtcNow);
        
        _output.WriteLine($"Timestamp: {usage.Timestamp}");
    }

    [Fact]
    public async Task GetSessionUsage_NonExistentSession_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<CodePod.Sdk.Exceptions.SessionNotFoundException>(
            () => Client.GetSessionUsageAsync(99999));
    }

    [Fact]
    public async Task MultipleSession_EachHasIndependentUsage()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session1 = await sessions.CreateSessionAsync(new SessionOptions { Name = "会话1" });
        SessionInfo session2 = await sessions.CreateSessionAsync(new SessionOptions { Name = "会话2" });

        // 在会话1执行更多工作
        for (int i = 0; i < 10; i++)
        {
            await Client.ExecuteCommandAsync(session1.Id, $"echo 'Session 1 work {i}'");
        }

        // 会话2只执行少量工作
        await Client.ExecuteCommandAsync(session2.Id, "echo 'Session 2 minimal'");

        // Act
        SessionUsage? usage1 = await Client.GetSessionUsageAsync(session1.Id);
        SessionUsage? usage2 = await Client.GetSessionUsageAsync(session2.Id);

        // Assert
        Assert.NotNull(usage1);
        Assert.NotNull(usage2);
        Assert.NotEqual(usage1.ContainerId, usage2.ContainerId);
        
        // 会话1应该有更多CPU使用
        _output.WriteLine($"Session 1 CPU: {usage1.CpuUsageNanos}");
        _output.WriteLine($"Session 2 CPU: {usage2.CpuUsageNanos}");
    }

}
