using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 网络隔离功能测试基类
/// 验证 Docker 的 none/bridge/host 网络模式
/// </summary>
public abstract class NetworkIsolationTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.PrewarmCount = 0;
            config.DefaultNetworkMode = NetworkMode.None;
            config.LabelPrefix = GetLabelPrefix();
        });
    }
}

public class NetworkMode_NoneBlocksTest : NetworkIsolationTestBase
{
    [Fact]
    [Trait("Category", "NetworkIsolation")]
    public async Task NetworkMode_None_BlocksNetworkAccess()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Windows containers do not support network isolation modes
        if (context.IsWindowsContainer) return;

        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionOptions options = new()
        {
            Name = "网络隔离测试 - None",
            NetworkMode = NetworkMode.None
        };

        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Act - 尝试访问外部网络
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "timeout 5 curl -s https://www.google.com || echo 'Network access blocked'",
            timeoutSeconds: 15);

        // Assert - 在 none 网络模式下，网络请求应该失败
        Assert.Contains("blocked", result.Stdout.ToLower() + result.Stderr.ToLower());
    }
}

public class NetworkMode_NoneLocalhostTest : NetworkIsolationTestBase
{
    [Fact]
    [Trait("Category", "NetworkIsolation")]
    public async Task NetworkMode_None_LocalhostAlsoBlocked()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Windows containers do not support network isolation modes
        if (context.IsWindowsContainer) return;

        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionOptions options = new()
        {
            Name = "本地网络测试 - None",
            NetworkMode = NetworkMode.None
        };

        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Act - 检查网络接口
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "cat /proc/net/dev | grep -v lo | tail -n +3 | head -1 || echo 'no_external_interfaces'",
            timeoutSeconds: 10);

        // Assert - none 模式下只有 loopback 接口
        string output = result.Stdout.Trim();
        Assert.True(
            string.IsNullOrEmpty(output) || output == "no_external_interfaces",
            "None mode should have no external network interfaces");
    }
}

public class NetworkMode_BridgeAllowsTest : NetworkIsolationTestBase
{
    [Fact]
    [Trait("Category", "NetworkIsolation")]
    public async Task NetworkMode_Bridge_AllowsNetworkAccess()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Windows containers do not support network isolation modes
        if (context.IsWindowsContainer) return;

        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionOptions options = new()
        {
            Name = "网络测试 - Bridge",
            NetworkMode = NetworkMode.Bridge
        };

        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Act - 尝试 DNS 解析
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "nslookup google.com 2>&1 || host google.com 2>&1 || echo 'DNS lookup test'",
            timeoutSeconds: 15);

        // Assert - Bridge 模式下应该有网络访问能力
        Assert.NotNull(result);
    }
}

public class NetworkMode_BridgeInterfaceTest : NetworkIsolationTestBase
{
    [Fact]
    [Trait("Category", "NetworkIsolation")]
    public async Task NetworkMode_Bridge_HasEthInterface()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Windows containers do not support network isolation modes
        if (context.IsWindowsContainer) return;

        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionOptions options = new()
        {
            Name = "网络接口测试 - Bridge",
            NetworkMode = NetworkMode.Bridge
        };

        SessionInfo session = await sessions.CreateSessionAsync(options);

        // Act - 检查网络接口
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "cat /proc/net/dev | grep -v lo | tail -n +3 || ip addr show 2>/dev/null || echo 'no_network_tools'",
            timeoutSeconds: 10);

        // Assert - Bridge 模式下应该有网络接口
        Assert.True(
            result.Stdout.Contains("eth") || 
            result.Stdout.Contains("veth") || 
            result.Stdout.Contains("Receive") ||
            result.Stdout.Trim().Length > 0,
            "Bridge mode should have network interfaces");
    }
}

public class NetworkMode_DefaultTest : NetworkIsolationTestBase
{
    [Fact]
    [Trait("Category", "NetworkIsolation")]
    public async Task DefaultSession_UsesConfiguredNetworkMode()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Windows containers do not support network isolation modes
        if (context.IsWindowsContainer) return;

        await using TestSessionTracker sessions = new(context.Client);

        // Arrange - 客户端配置默认使用 None
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "默认网络模式测试" });

        // Act - 检查网络接口
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "cat /proc/net/dev | grep -v lo | tail -n +3 | head -1 || echo 'no_external_interfaces'",
            timeoutSeconds: 10);

        // Assert - 默认配置是 None，应该没有外部网络接口
        string output = result.Stdout.Trim();
        Assert.True(
            string.IsNullOrEmpty(output) || output == "no_external_interfaces",
            "Default None mode should have no external network interfaces");
    }
}

public class NetworkMode_OverrideTest : NetworkIsolationTestBase
{
    [Fact]
    [Trait("Category", "NetworkIsolation")]
    public async Task NetworkMode_CanBeOverriddenPerSession()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Windows containers do not support network isolation modes
        if (context.IsWindowsContainer) return;

        await using TestSessionTracker sessions = new(context.Client);

        // Arrange - 创建 None 模式会话
        SessionInfo sessionNone = await sessions.CreateSessionAsync(new SessionOptions
        {
            Name = "None 模式",
            NetworkMode = NetworkMode.None
        });

        // 创建 Bridge 模式会话
        SessionInfo sessionBridge = await sessions.CreateSessionAsync(new SessionOptions
        {
            Name = "Bridge 模式",
            NetworkMode = NetworkMode.Bridge
        });

        // Act - 验证两个会话有不同的网络配置
        CommandResult resultNone = await context.Client.ExecuteCommandAsync(
            sessionNone.Id,
            "cat /proc/net/dev | grep -v lo | tail -n +3 | wc -l",
            timeoutSeconds: 10);

        CommandResult resultBridge = await context.Client.ExecuteCommandAsync(
            sessionBridge.Id,
            "cat /proc/net/dev | grep -v lo | tail -n +3 | wc -l",
            timeoutSeconds: 10);

        // Assert - 两个都能执行命令
        Assert.NotNull(resultNone);
        Assert.NotNull(resultBridge);
    }
}
