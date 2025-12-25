using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 使用量计量功能测试
/// 验证 Docker Stats API 获取资源使用情况
/// </summary>
public class UsageMeteringTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private CodePodClient _client = null!;
    private ILoggerFactory _loggerFactory = null!;
    private bool _isWindowsContainer;

    public UsageMeteringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        CodePodTestSettings settings = TestSettings.Load();
        _isWindowsContainer = settings.IsWindowsContainer;
        var workDir = _isWindowsContainer ? "C:\\app" : "/app";
        var image = _isWindowsContainer ? settings.DotnetSdkWindowsImage : settings.DotnetSdkLinuxImage;

        CodePodConfig config = new()
        {
            DockerEndpoint = settings.DockerEndpoint,
            IsWindowsContainer = _isWindowsContainer,
            Image = image,
            PrewarmCount = 2,
            MaxContainers = 10,
            SessionTimeoutSeconds = 300,
            WorkDir = workDir,
            LabelPrefix = "codepod-metering-test"
        };

        _client = new CodePodClientBuilder()
            .WithConfig(config)
            .WithLogging(_loggerFactory)
            .Build();

        await _client.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            IReadOnlyList<SessionInfo> sessions = await _client.GetAllSessionsAsync();
            foreach (SessionInfo session in sessions)
            {
                try
                {
                    await _client.DestroySessionAsync(session.Id);
                }
                catch { }
            }
            await _client.DeleteAllContainersAsync();
        }
        catch { }
        finally
        {
            _client.Dispose();
            _loggerFactory.Dispose();
        }
    }

    [Fact]
    public async Task GetSessionUsage_ReturnsStats()
    {
        // Arrange
        SessionInfo session = await _client.CreateSessionAsync("使用量测试");
        await WaitForSessionReadyAsync(session.Id);

        // 执行一些命令产生使用量
        await _client.ExecuteCommandAsync(session.Id, "echo 'test'");

        // Act
        SessionUsage? usage = await _client.GetSessionUsageAsync(session.Id);

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
        SessionInfo session = await _client.CreateSessionAsync("活动使用量测试");
        await WaitForSessionReadyAsync(session.Id);

        // 获取初始使用量
        SessionUsage? usageBefore = await _client.GetSessionUsageAsync(session.Id);

        // 执行一些工作
        for (int i = 0; i < 5; i++)
        {
            await _client.ExecuteCommandAsync(session.Id, $"echo 'Work iteration {i}'");
        }

        // 获取工作后的使用量
        SessionUsage? usageAfter = await _client.GetSessionUsageAsync(session.Id);

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
        SessionInfo session = await _client.CreateSessionAsync("内存密集测试");
        await WaitForSessionReadyAsync(session.Id);

        // 获取初始使用量
        SessionUsage? usageBefore = await _client.GetSessionUsageAsync(session.Id);

        // 执行内存密集型工作（创建大文件）
        var command = _isWindowsContainer
            ? "$p = Join-Path $PWD 'testfile.bin'; $bytes = New-Object byte[] (10MB); [IO.File]::WriteAllBytes($p, $bytes); Remove-Item -Force $p"
            : "dd if=/dev/zero of=/tmp/testfile bs=1M count=10 2>/dev/null && rm /tmp/testfile";

        await _client.ExecuteCommandAsync(session.Id, command, timeoutSeconds: 30);

        // 获取工作后的使用量
        SessionUsage? usageAfter = await _client.GetSessionUsageAsync(session.Id);

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
        SessionInfo session = await _client.CreateSessionAsync("时间戳测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act
        SessionUsage? usage = await _client.GetSessionUsageAsync(session.Id);

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
            () => _client.GetSessionUsageAsync(99999));
    }

    [Fact]
    public async Task MultipleSession_EachHasIndependentUsage()
    {
        // Arrange
        SessionInfo session1 = await _client.CreateSessionAsync("会话1");
        SessionInfo session2 = await _client.CreateSessionAsync("会话2");
        await WaitForSessionReadyAsync(session1.Id);
        await WaitForSessionReadyAsync(session2.Id);

        // 在会话1执行更多工作
        for (int i = 0; i < 10; i++)
        {
            await _client.ExecuteCommandAsync(session1.Id, $"echo 'Session 1 work {i}'");
        }

        // 会话2只执行少量工作
        await _client.ExecuteCommandAsync(session2.Id, "echo 'Session 2 minimal'");

        // Act
        SessionUsage? usage1 = await _client.GetSessionUsageAsync(session1.Id);
        SessionUsage? usage2 = await _client.GetSessionUsageAsync(session2.Id);

        // Assert
        Assert.NotNull(usage1);
        Assert.NotNull(usage2);
        Assert.NotEqual(usage1.ContainerId, usage2.ContainerId);
        
        // 会话1应该有更多CPU使用
        _output.WriteLine($"Session 1 CPU: {usage1.CpuUsageNanos}");
        _output.WriteLine($"Session 2 CPU: {usage2.CpuUsageNanos}");
    }

    private async Task<SessionInfo> WaitForSessionReadyAsync(int sessionId, int maxWaitSeconds = 30)
    {
        for (int i = 0; i < maxWaitSeconds * 2; i++)
        {
            SessionInfo session = await _client.GetSessionAsync(sessionId);
            if (!string.IsNullOrEmpty(session.ContainerId))
            {
                return session;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Session {sessionId} did not become ready within {maxWaitSeconds} seconds");
    }
}
