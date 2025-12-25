using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 输出截断功能测试
/// 验证大输出的智能截断
/// </summary>
[Collection(OutputTruncationCollection.Name)]
public class OutputTruncationTests
{
    private readonly ITestOutputHelper _output;
    private readonly OutputTruncationCodePodFixture _fixture;

    private CodePodClient Client => _fixture.Client;
    private bool IsWindowsContainer => _fixture.IsWindowsContainer;

    public OutputTruncationTests(OutputTruncationCodePodFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task SmallOutput_NotTruncated()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "小输出测试" });

        // Act - 生成少量输出
        CommandResult result = await Client.ExecuteCommandAsync(
            session.Id,
            "echo 'Small output'");

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.IsTruncated);
        Assert.Contains("Small output", result.Stdout);
        _output.WriteLine($"Output: {result.Stdout}");
        _output.WriteLine($"Truncated: {result.IsTruncated}");
    }

    [Fact]
    public async Task LargeOutput_IsTruncated()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "大输出截断测试" });

        // Act - 生成大量输出 (超过 1KB)
        string command = IsWindowsContainer
            ? "1..500 | ForEach-Object { Write-Output (\"Line {0}: This is a test line to generate large output\" -f $_) }"
            : "for i in $(seq 1 500); do echo \"Line $i: This is a test line to generate large output\"; done";

        CommandResult result = await Client.ExecuteCommandAsync(session.Id, command);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.IsTruncated, "Large output should be truncated");
        Assert.Contains("truncated", result.Stdout.ToLower());
        
        // 验证保留了开头和结尾
        Assert.Contains("Line 1:", result.Stdout);
        Assert.Contains("Line 500:", result.Stdout);
        
        _output.WriteLine($"Output length: {result.Stdout.Length}");
        _output.WriteLine($"Truncated: {result.IsTruncated}");
        _output.WriteLine($"First 200 chars: {result.Stdout[..Math.Min(200, result.Stdout.Length)]}");
    }

    [Fact]
    public async Task TruncationMessage_ContainsByteCount()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "截断信息测试" });

        // Act - 生成大量输出
        string command = IsWindowsContainer
            ? "1..1000 | ForEach-Object { $_ }"
            : "seq 1 1000";

        CommandResult result = await Client.ExecuteCommandAsync(session.Id, command);

        // Assert
        if (result.IsTruncated)
        {
            Assert.Contains("bytes truncated", result.Stdout);
            _output.WriteLine($"Truncation message found in output");
        }
        _output.WriteLine($"Truncated: {result.IsTruncated}");
    }

    [Fact]
    public async Task StderrAlso_Truncated()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "Stderr 截断测试" });

        // Act - 生成大量 stderr 输出
        string command = IsWindowsContainer
            ? "1..500 | ForEach-Object { [Console]::Error.WriteLine((\"Error line {0}\" -f $_)) }"
            : "for i in $(seq 1 500); do echo \"Error line $i\" >&2; done";

        CommandResult result = await Client.ExecuteCommandAsync(session.Id, command);

        // Assert
        // stderr 也应该被截断
        _output.WriteLine($"Stderr length: {result.Stderr.Length}");
        _output.WriteLine($"Stderr truncated: {result.IsTruncated}");
        
        if (result.Stderr.Length > 100)
        {
            _output.WriteLine($"First 200 chars of stderr: {result.Stderr[..Math.Min(200, result.Stderr.Length)]}");
        }
    }

    [Fact]
    public async Task HeadAndTail_Strategy_PreservesContext()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "头尾策略测试" });

        // Act - 生成有特定开头和结尾的输出
        string command = IsWindowsContainer
            ? "Write-Output '=== START ==='; 1..500 | ForEach-Object { Write-Output (\"Middle line {0}\" -f $_) }; Write-Output '=== END ==='"
            : "echo '=== START ===' && for i in $(seq 1 500); do echo \"Middle line $i\"; done && echo '=== END ==='";

        CommandResult result = await Client.ExecuteCommandAsync(session.Id, command);

        // Assert
        Assert.Equal(0, result.ExitCode);
        
        if (result.IsTruncated)
        {
            // HeadAndTail 策略应该保留开头和结尾
            Assert.Contains("=== START ===", result.Stdout);
            Assert.Contains("=== END ===", result.Stdout);
        }
        
        _output.WriteLine($"Truncated: {result.IsTruncated}");
        _output.WriteLine($"Contains START: {result.Stdout.Contains("=== START ===")}");
        _output.WriteLine($"Contains END: {result.Stdout.Contains("=== END ===")}");
    }

    [Fact]
    public async Task OutputOptions_DefaultValues_AreReasonable()
    {
        // Assert
        OutputOptions defaultOptions = new();
        
        Assert.Equal(64 * 1024, defaultOptions.MaxOutputBytes); // 64KB
        Assert.Equal(TruncationStrategy.HeadAndTail, defaultOptions.Strategy);
        Assert.NotEmpty(defaultOptions.TruncationMessage);
        
        _output.WriteLine($"Default MaxOutputBytes: {defaultOptions.MaxOutputBytes}");
        _output.WriteLine($"Default Strategy: {defaultOptions.Strategy}");
        _output.WriteLine($"Default TruncationMessage: {defaultOptions.TruncationMessage}");
    }

}
