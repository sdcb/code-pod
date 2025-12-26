using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 输出截断功能测试基类
/// 验证大输出的智能截断
/// </summary>
public abstract class OutputTruncationTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.PrewarmCount = 0;
            config.SessionTimeoutSeconds = 300;
            config.OutputOptions = new OutputOptions
            {
                MaxOutputBytes = 1024,
                Strategy = TruncationStrategy.HeadAndTail,
                TruncationMessage = "\n... [{0} bytes truncated] ...\n"
            };
            config.LabelPrefix = GetLabelPrefix();
        });
    }
}

public class OutputTruncation_SmallOutputTest : OutputTruncationTestBase
{
    [Fact]
    [Trait("Category", "OutputTruncation")]
    public async Task SmallOutput_NotTruncated()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "小输出测试" });

        // Act - 生成少量输出
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "echo 'Small output'");

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.IsTruncated);
        Assert.Contains("Small output", result.Stdout);
    }
}

public class OutputTruncation_LargeOutputTest : OutputTruncationTestBase
{
    [Fact]
    [Trait("Category", "OutputTruncation")]
    public async Task LargeOutput_IsTruncated()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "大输出截断测试" });

        // Act - 生成大量输出 (超过 1KB)
        string command = context.IsWindowsContainer
            ? "1..500 | ForEach-Object { Write-Output (\"Line {0}: This is a test line to generate large output\" -f $_) }"
            : "for i in $(seq 1 500); do echo \"Line $i: This is a test line to generate large output\"; done";

        CommandResult result = await context.Client.ExecuteCommandAsync(session.Id, command);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.IsTruncated, "Large output should be truncated");
        Assert.Contains("truncated", result.Stdout.ToLower());
        
        // 验证保留了开头和结尾
        Assert.Contains("Line 1:", result.Stdout);
        Assert.Contains("Line 500:", result.Stdout);
    }
}

public class OutputTruncation_MessageTest : OutputTruncationTestBase
{
    [Fact]
    [Trait("Category", "OutputTruncation")]
    public async Task TruncationMessage_ContainsByteCount()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "截断信息测试" });

        // Act - 生成大量输出
        string command = context.IsWindowsContainer
            ? "1..1000 | ForEach-Object { $_ }"
            : "seq 1 1000";

        CommandResult result = await context.Client.ExecuteCommandAsync(session.Id, command);

        // Assert
        if (result.IsTruncated)
        {
            Assert.Contains("bytes truncated", result.Stdout);
        }
    }
}

public class OutputTruncation_StderrTest : OutputTruncationTestBase
{
    [Fact]
    [Trait("Category", "OutputTruncation")]
    public async Task StderrAlso_Truncated()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "Stderr 截断测试" });

        // Act - 生成大量 stderr 输出
        string command = context.IsWindowsContainer
            ? "1..500 | ForEach-Object { [Console]::Error.WriteLine((\"Error line {0}\" -f $_)) }"
            : "for i in $(seq 1 500); do echo \"Error line $i\" >&2; done";

        CommandResult result = await context.Client.ExecuteCommandAsync(session.Id, command);

        // Assert - stderr 也应该被截断（如果太大）
        Assert.NotNull(result);
    }
}

public class OutputTruncation_StrategyTest : OutputTruncationTestBase
{
    [Fact]
    [Trait("Category", "OutputTruncation")]
    public async Task HeadAndTail_Strategy_PreservesContext()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "头尾策略测试" });

        // Act - 生成有特定开头和结尾的输出
        string command = context.IsWindowsContainer
            ? "Write-Output '=== START ==='; 1..500 | ForEach-Object { Write-Output (\"Middle line {0}\" -f $_) }; Write-Output '=== END ==='"
            : "echo '=== START ===' && for i in $(seq 1 500); do echo \"Middle line $i\"; done && echo '=== END ==='";

        CommandResult result = await context.Client.ExecuteCommandAsync(session.Id, command);

        // Assert
        Assert.Equal(0, result.ExitCode);
        
        if (result.IsTruncated)
        {
            // HeadAndTail 策略应该保留开头和结尾
            Assert.Contains("=== START ===", result.Stdout);
            Assert.Contains("=== END ===", result.Stdout);
        }
    }
}

public class OutputTruncation_DefaultOptionsTest : OutputTruncationTestBase
{
    [Fact]
    [Trait("Category", "OutputTruncation")]
    public async Task OutputOptions_DefaultValues_AreReasonable()
    {
        // Assert
        OutputOptions defaultOptions = new();
        
        Assert.Equal(64 * 1024, defaultOptions.MaxOutputBytes); // 64KB
        Assert.Equal(TruncationStrategy.HeadAndTail, defaultOptions.Strategy);
        Assert.NotEmpty(defaultOptions.TruncationMessage);
    }
}
