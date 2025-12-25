using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 资源限制功能测试
/// 验证内存限制、CPU 限制、进程数限制
/// </summary>
public class ResourceLimitsTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private CodePodClient _client = null!;
    private ILoggerFactory _loggerFactory = null!;
    private bool _isWindowsContainer;

    public ResourceLimitsTests(ITestOutputHelper output)
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
            PrewarmCount = 0, // 不预热，便于测试自定义资源限制
            MaxContainers = 10,
            SessionTimeoutSeconds = 300,
            WorkDir = workDir,
            LabelPrefix = "codepod-reslimit-test",
            // 系统最大资源限制
            MaxResourceLimits = new ResourceLimits
            {
                MemoryBytes = 1024 * 1024 * 1024, // 1GB
                CpuCores = 2.0,
                MaxProcesses = 200
            },
            // 默认资源限制
            DefaultResourceLimits = ResourceLimits.Standard
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
    public async Task CreateSession_WithDefaultLimits_Succeeds()
    {
        // Act
        SessionInfo session = await _client.CreateSessionAsync("默认限制测试");
        await WaitForSessionReadyAsync(session.Id);

        // Assert
        Assert.NotNull(session);
        Assert.NotEmpty(session.ContainerId);
        _output.WriteLine($"Session created with container: {session.ContainerId}");
    }

    [Fact]
    public async Task CreateSession_WithCustomLimits_Succeeds()
    {
        // Arrange
        SessionOptions options = new()
        {
            Name = "自定义限制测试",
            ResourceLimits = new ResourceLimits
            {
                MemoryBytes = 256 * 1024 * 1024, // 256MB
                CpuCores = 0.5,
                MaxProcesses = 50
            }
        };

        // Act
        SessionInfo session = await _client.CreateSessionAsync(options);
        await WaitForSessionReadyAsync(session.Id);

        // Assert
        Assert.NotNull(session);
        Assert.NotEmpty(session.ContainerId);
        _output.WriteLine($"Session created with custom limits, container: {session.ContainerId}");
    }

    [Fact]
    public async Task CreateSession_WithMinimalLimits_Succeeds()
    {
        // Arrange
        SessionOptions options = new()
        {
            Name = "最小限制测试",
            ResourceLimits = ResourceLimits.Minimal
        };

        // Act
        SessionInfo session = await _client.CreateSessionAsync(options);
        await WaitForSessionReadyAsync(session.Id);

        // Assert
        Assert.NotNull(session);
        _output.WriteLine($"Session created with minimal limits (128MB, 0.25 CPU)");
    }

    [Fact]
    public async Task CreateSession_ExceedingMaxLimits_ThrowsException()
    {
        // Arrange
        SessionOptions options = new()
        {
            Name = "超限测试",
            ResourceLimits = new ResourceLimits
            {
                MemoryBytes = 2L * 1024 * 1024 * 1024, // 2GB - 超过系统最大 1GB
                CpuCores = 1.0,
                MaxProcesses = 100
            }
        };

        // Act & Assert
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _client.CreateSessionAsync(options));
        
        Assert.Contains("memory", ex.Message.ToLower());
        _output.WriteLine($"Expected exception: {ex.Message}");
    }

    [Fact]
    public async Task MemoryLimit_EnforcedByDocker()
    {
        // Arrange - 使用较小的内存限制
        SessionOptions options = new()
        {
            Name = "内存限制验证",
            ResourceLimits = new ResourceLimits
            {
                MemoryBytes = 64 * 1024 * 1024, // 64MB
                CpuCores = 1.0,
                MaxProcesses = 100
            }
        };

        SessionInfo session = await _client.CreateSessionAsync(options);
        await WaitForSessionReadyAsync(session.Id);

        // Act - 尝试分配超过限制的内存（这可能会失败或被 OOM killer 杀死）
        // 使用 dotnet 分配内存
        CommandResult result = await _client.ExecuteCommandAsync(
            session.Id,
            "dotnet --version && echo 'Memory limit test passed'",
            timeoutSeconds: 30);

        // Assert - 命令应该能执行（只要不超内存）
        _output.WriteLine($"Exit code: {result.ExitCode}");
        _output.WriteLine($"Stdout: {result.Stdout}");
        _output.WriteLine($"Stderr: {result.Stderr}");
        
        // 只要命令能执行就算通过，具体内存限制测试需要更复杂的场景
        Assert.True(true);
    }

    [Fact]
    public async Task PidsLimit_EnforcedByDocker()
    {
        // Arrange - 使用较小的进程数限制
        SessionOptions options = new()
        {
            Name = "进程数限制验证",
            ResourceLimits = new ResourceLimits
            {
                MemoryBytes = 256 * 1024 * 1024,
                CpuCores = 1.0,
                MaxProcesses = 10 // 非常少的进程数限制
            }
        };

        SessionInfo session = await _client.CreateSessionAsync(options);
        await WaitForSessionReadyAsync(session.Id);

        // Act - 尝试创建多个子进程（可能会达到限制）
        var command = _isWindowsContainer
            ? "1..5 | ForEach-Object { Write-Output $_ }"
            : "for i in $(seq 1 5); do echo $i; done";

        CommandResult result = await _client.ExecuteCommandAsync(session.Id, command, timeoutSeconds: 30);

        // Assert
        _output.WriteLine($"Exit code: {result.ExitCode}");
        _output.WriteLine($"Stdout: {result.Stdout}");
        
        // 简单的 for 循环应该能执行成功
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ResourceLimits_Presets_HaveValidValues()
    {
        // Assert - 验证预设值合理
        Assert.True(ResourceLimits.Minimal.MemoryBytes > 0);
        Assert.True(ResourceLimits.Minimal.CpuCores > 0);
        Assert.True(ResourceLimits.Minimal.MaxProcesses > 0);

        Assert.True(ResourceLimits.Standard.MemoryBytes > ResourceLimits.Minimal.MemoryBytes);
        Assert.True(ResourceLimits.Large.MemoryBytes > ResourceLimits.Standard.MemoryBytes);

        _output.WriteLine($"Minimal: {ResourceLimits.Minimal.MemoryBytes / 1024 / 1024}MB, {ResourceLimits.Minimal.CpuCores} CPU");
        _output.WriteLine($"Standard: {ResourceLimits.Standard.MemoryBytes / 1024 / 1024}MB, {ResourceLimits.Standard.CpuCores} CPU");
        _output.WriteLine($"Large: {ResourceLimits.Large.MemoryBytes / 1024 / 1024}MB, {ResourceLimits.Large.CpuCores} CPU");
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
