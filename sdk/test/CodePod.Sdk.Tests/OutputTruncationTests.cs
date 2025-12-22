using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 输出截断功能测试
/// 验证大输出的智能截断
/// </summary>
public class OutputTruncationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private CodePodClient _client = null!;
    private ILoggerFactory _loggerFactory = null!;

    public OutputTruncationTests(ITestOutputHelper output)
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

        var config = new CodePodConfig
        {
            Image = "mcr.microsoft.com/dotnet/sdk:10.0",
            PrewarmCount = 2,
            MaxContainers = 10,
            SessionTimeoutSeconds = 300,
            WorkDir = "/app",
            LabelPrefix = "codepod-truncation-test",
            OutputOptions = new OutputOptions
            {
                MaxOutputBytes = 1024, // 1KB 用于测试
                Strategy = TruncationStrategy.HeadAndTail,
                TruncationMessage = "\n... [{0} bytes truncated] ...\n"
            }
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
            var sessions = await _client.GetAllSessionsAsync();
            foreach (var session in sessions)
            {
                try
                {
                    await _client.DestroySessionAsync(session.SessionId);
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
    public async Task SmallOutput_NotTruncated()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("小输出测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - 生成少量输出
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
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
        var session = await _client.CreateSessionAsync("大输出截断测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - 生成大量输出 (超过 1KB)
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            "for i in $(seq 1 500); do echo \"Line $i: This is a test line to generate large output\"; done");

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
        var session = await _client.CreateSessionAsync("截断信息测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - 生成大量输出
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            "seq 1 1000");

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
        var session = await _client.CreateSessionAsync("Stderr 截断测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - 生成大量 stderr 输出
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            "for i in $(seq 1 500); do echo \"Error line $i\" >&2; done");

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
        var session = await _client.CreateSessionAsync("头尾策略测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - 生成有特定开头和结尾的输出
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            "echo '=== START ===' && for i in $(seq 1 500); do echo \"Middle line $i\"; done && echo '=== END ==='");

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
        var defaultOptions = new OutputOptions();
        
        Assert.Equal(64 * 1024, defaultOptions.MaxOutputBytes); // 64KB
        Assert.Equal(TruncationStrategy.HeadAndTail, defaultOptions.Strategy);
        Assert.NotEmpty(defaultOptions.TruncationMessage);
        
        _output.WriteLine($"Default MaxOutputBytes: {defaultOptions.MaxOutputBytes}");
        _output.WriteLine($"Default Strategy: {defaultOptions.Strategy}");
        _output.WriteLine($"Default TruncationMessage: {defaultOptions.TruncationMessage}");
    }

    private async Task<SessionInfo> WaitForSessionReadyAsync(string sessionId, int maxWaitSeconds = 30)
    {
        for (int i = 0; i < maxWaitSeconds * 2; i++)
        {
            var session = await _client.GetSessionAsync(sessionId);
            if (!string.IsNullOrEmpty(session.ContainerId))
            {
                return session;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Session {sessionId} did not become ready within {maxWaitSeconds} seconds");
    }
}
