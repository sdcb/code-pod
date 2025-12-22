using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 命令数组执行测试
/// 验证直接传递命令数组（不经过 shell 包装）
/// </summary>
public class CommandArrayTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private CodePodClient _client = null!;
    private ILoggerFactory _loggerFactory = null!;

    public CommandArrayTests(ITestOutputHelper output)
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
            Image = "python:3.12-slim", // 使用 Python 镜像来测试命令数组
            PrewarmCount = 0, // 不预热，每个测试独立
            MaxContainers = 10,
            SessionTimeoutSeconds = 300,
            WorkDir = "/app",
            LabelPrefix = "codepod-cmdarray-test"
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
    public async Task ExecuteCommandArray_BasicCommand_Works()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("命令数组基础测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - 使用命令数组
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            ["echo", "Hello from command array!"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from command array!", result.Stdout);
        _output.WriteLine($"Output: {result.Stdout}");
    }

    [Fact]
    public async Task ExecuteCommandArray_AvoidShellEscaping()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("避免转义测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - 包含特殊字符的参数，使用数组形式不需要转义
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            ["echo", "Hello \"World\" with 'quotes' and $variables"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        // 特殊字符应该被原样输出
        Assert.Contains("\"World\"", result.Stdout);
        Assert.Contains("'quotes'", result.Stdout);
        Assert.Contains("$variables", result.Stdout);
        _output.WriteLine($"Output: {result.Stdout}");
    }

    [Fact]
    public async Task ExecuteCommandArray_PythonOneliner()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("Python 一行代码测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act - AI 常用的 Python 一行代码执行
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            ["python", "-c", "print('Hello from Python!\\nLine 2\\nLine 3')"]);

        // Assert
        _output.WriteLine($"Exit code: {result.ExitCode}");
        _output.WriteLine($"Stdout: {result.Stdout}");
        _output.WriteLine($"Stderr: {result.Stderr}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from Python!", result.Stdout);
    }

    [Fact]
    public async Task ExecuteCommandArray_PythonVersion()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("Python 命令数组测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            ["python", "--version"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"Python \d+\.\d+", result.Stdout + result.Stderr); // Python 版本格式
        _output.WriteLine($"Python version: {(result.Stdout + result.Stderr).Trim()}");
    }

    [Fact]
    public async Task ExecuteCommandArray_WithWorkingDirectory()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("工作目录测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // 先创建目录
        await _client.ExecuteCommandAsync(session.SessionId, "mkdir -p /tmp/testdir");

        // Act - 在指定目录执行命令数组
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            ["pwd"],
            workingDirectory: "/tmp/testdir");

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/tmp/testdir", result.Stdout);
        _output.WriteLine($"Working directory: {result.Stdout.Trim()}");
    }

    [Fact]
    public async Task ExecuteCommandArrayStream_Works()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("流式命令数组测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // Act
        var outputs = new List<string>();
        await foreach (var evt in _client.ExecuteCommandStreamAsync(
            session.SessionId,
            ["bash", "-c", "for i in 1 2 3; do echo Line$i; done"]))
        {
            if (evt.Type == CommandOutputType.Stdout)
            {
                outputs.Add(evt.Data ?? "");
                _output.WriteLine($"Received: {evt.Data}");
            }
        }

        // Assert
        var allOutput = string.Join("", outputs);
        Assert.Contains("Line1", allOutput);
        Assert.Contains("Line2", allOutput);
        Assert.Contains("Line3", allOutput);
    }

    [Fact]
    public async Task ExecuteCommandArray_ComplexPythonCode()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("复杂 Python 代码测试");
        await WaitForSessionReadyAsync(session.SessionId);

        // 这是 AI 经常生成的多行 Python 代码
        var pythonCode = @"
import json
data = {'name': 'CodePod', 'version': '1.0', 'features': ['resource_limits', 'network_isolation']}
print(json.dumps(data, indent=2))
";

        // Act - 使用命令数组，代码中的引号和特殊字符不需要转义
        var result = await _client.ExecuteCommandAsync(
            session.SessionId,
            ["python", "-c", pythonCode]);

        // Assert
        _output.WriteLine($"Exit code: {result.ExitCode}");
        _output.WriteLine($"Stdout: {result.Stdout}");
        _output.WriteLine($"Stderr: {result.Stderr}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CodePod", result.Stdout);
        Assert.Contains("resource_limits", result.Stdout);
    }

    [Fact]
    public async Task CommandString_VsCommandArray_Comparison()
    {
        // Arrange
        var session = await _client.CreateSessionAsync("字符串 vs 数组对比");
        await WaitForSessionReadyAsync(session.SessionId);

        // 测试相同命令的两种方式

        // 方式1: 字符串（需要转义）
        var resultString = await _client.ExecuteCommandAsync(
            session.SessionId,
            "echo 'Hello World'");

        // 方式2: 数组（不需要转义）
        var resultArray = await _client.ExecuteCommandAsync(
            session.SessionId,
            ["echo", "Hello World"]);

        // Assert - 两种方式结果应该相同
        _output.WriteLine($"String: {resultString.Stdout.Trim()}");
        _output.WriteLine($"Array: {resultArray.Stdout.Trim()}");
        
        Assert.Equal(resultString.Stdout.Trim(), resultArray.Stdout.Trim());
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
