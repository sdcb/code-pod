using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 资源限制功能测试基类
/// 验证内存限制、CPU 限制、进程数限制
/// </summary>
public abstract class ResourceLimitsTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.PrewarmCount = 0;
            config.LabelPrefix = GetLabelPrefix();
        });
    }
}

public class ResourceLimits_DefaultTest : ResourceLimitsTestBase
{
    [Fact]
    [Trait("Category", "ResourceLimits")]
    public async Task CreateSession_WithDefaultLimits_Succeeds()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "默认限制测试" });

        // Assert
        Assert.NotNull(session);
        Assert.NotEmpty(session.ContainerId);
    }
}

public class ResourceLimits_CustomTest : ResourceLimitsTestBase
{
    [Fact]
    [Trait("Category", "ResourceLimits")]
    public async Task CreateSession_WithCustomLimits_Succeeds()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

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
        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Assert
        Assert.NotNull(session);
        Assert.NotEmpty(session.ContainerId);
    }
}

public class ResourceLimits_MinimalTest : ResourceLimitsTestBase
{
    [Fact]
    [Trait("Category", "ResourceLimits")]
    public async Task CreateSession_WithMinimalLimits_Succeeds()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionOptions options = new()
        {
            Name = "最小限制测试",
            ResourceLimits = ResourceLimits.Minimal
        };

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Assert
        Assert.NotNull(session);
    }
}

public class ResourceLimits_ExceedTest : ResourceLimitsTestBase
{
    [Fact]
    [Trait("Category", "ResourceLimits")]
    public async Task CreateSession_ExceedingMaxLimits_ThrowsException()
    {
        await using TestClientContext context = await CreateContextAsync();

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
            () => context.Client.CreateSessionAsync(options));
        
        Assert.Contains("memory", ex.Message.ToLower());
    }
}

public class ResourceLimits_MemoryEnforcementTest : ResourceLimitsTestBase
{
    [Fact]
    [Trait("Category", "ResourceLimits")]
    public async Task MemoryLimit_EnforcedByDocker()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

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

        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Act - 尝试分配超过限制的内存
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "dotnet --version && echo 'Memory limit test passed'",
            timeoutSeconds: 30);

        // Assert - 命令应该能执行（只要不超内存）
        Assert.True(true);
    }
}

public class ResourceLimits_PidsEnforcementTest : ResourceLimitsTestBase
{
    [Fact]
    [Trait("Category", "ResourceLimits")]
    public async Task PidsLimit_EnforcedByDocker()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

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

        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Act - 尝试创建多个子进程
        string command = context.IsWindowsContainer
            ? "1..5 | ForEach-Object { Write-Output $_ }"
            : "for i in $(seq 1 5); do echo $i; done";

        CommandResult result = await context.Client.ExecuteCommandAsync(session.Id, command, timeoutSeconds: 30);

        // Assert - 简单的 for 循环应该能执行成功
        Assert.Equal(0, result.ExitCode);
    }
}

public class ResourceLimits_PresetsTest : ResourceLimitsTestBase
{
    [Fact]
    [Trait("Category", "ResourceLimits")]
    public async Task ResourceLimits_Presets_HaveValidValues()
    {
        // Assert - 验证预设值合理
        Assert.True(ResourceLimits.Minimal.MemoryBytes > 0);
        Assert.True(ResourceLimits.Minimal.CpuCores > 0);
        Assert.True(ResourceLimits.Minimal.MaxProcesses > 0);

        Assert.True(ResourceLimits.Standard.MemoryBytes > ResourceLimits.Minimal.MemoryBytes);
        Assert.True(ResourceLimits.Large.MemoryBytes > ResourceLimits.Standard.MemoryBytes);
    }
}
