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
    private bool _isWindowsContainer;
    private string _workDir = "/app";

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

        CodePodTestSettings settings = TestSettings.Load();
        _isWindowsContainer = settings.IsWindowsContainer;
        _workDir = _isWindowsContainer ? "C:\\app" : "/app";

        var image = _isWindowsContainer ? settings.PythonWindowsImage : settings.PythonLinuxImage;

        var config = new CodePodConfig
        {
            DockerEndpoint = settings.DockerEndpoint,
            IsWindowsContainer = _isWindowsContainer,
            Image = image, // 使用 Python 镜像来测试命令数组
            PrewarmCount = 0, // 不预热，每个测试独立
            MaxContainers = 10,
            SessionTimeoutSeconds = 300,
            WorkDir = _workDir,
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
    public async Task ExecuteCommandArray_BasicCommand_Works()
    {
        // Arrange
        SessionInfo session = await _client.CreateSessionAsync("命令数组基础测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act - 使用命令数组
        CommandResult result = await _client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print('Hello from command array!')"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from command array!", result.Stdout);
        _output.WriteLine($"Output: {result.Stdout}");
    }

    [Fact]
    public async Task ExecuteCommandArray_AvoidShellEscaping()
    {
        // Arrange
        SessionInfo session = await _client.CreateSessionAsync("避免转义测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act - 包含特殊字符的参数，使用数组形式不需要转义
        CommandResult result = await _client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print(\"Hello \\\"World\\\" with 'quotes' and $variables\")"]);

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
        SessionInfo session = await _client.CreateSessionAsync("Python 一行代码测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act - AI 常用的 Python 一行代码执行
        CommandResult result = await _client.ExecuteCommandAsync(
            session.Id,
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
        SessionInfo session = await _client.CreateSessionAsync("Python 命令数组测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act
        CommandResult result = await _client.ExecuteCommandAsync(
            session.Id,
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
        SessionInfo session = await _client.CreateSessionAsync("工作目录测试");
        await WaitForSessionReadyAsync(session.Id);

        var targetDir = GetWorkPath("testdir");
        await _client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", $"import os; os.makedirs(r\"{EscapeForPythonRawString(targetDir)}\", exist_ok=True)"]);

        // Act - 在指定目录执行命令数组
        CommandResult result = await _client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "import os; print(os.getcwd())"],
            workingDirectory: targetDir);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("testdir", result.Stdout, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"Working directory: {result.Stdout.Trim()}");
    }

    [Fact]
    public async Task ExecuteCommandArrayStream_Works()
    {
        // Arrange
        SessionInfo session = await _client.CreateSessionAsync("流式命令数组测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act
        var outputs = new List<string>();
        await foreach (CommandOutputEvent evt in _client.ExecuteCommandStreamAsync(
            session.Id,
            ["python", "-c", "for i in range(1,4): print(f'Line{i}')"]))
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
        SessionInfo session = await _client.CreateSessionAsync("复杂 Python 代码测试");
        await WaitForSessionReadyAsync(session.Id);

        // 这是 AI 经常生成的多行 Python 代码
        var pythonCode = @"
import json
data = {'name': 'CodePod', 'version': '1.0', 'features': ['resource_limits', 'network_isolation']}
print(json.dumps(data, indent=2))
";

        // Act - 使用命令数组，代码中的引号和特殊字符不需要转义
        CommandResult result = await _client.ExecuteCommandAsync(
            session.Id,
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
        SessionInfo session = await _client.CreateSessionAsync("字符串 vs 数组对比");
        await WaitForSessionReadyAsync(session.Id);

        // 测试相同命令的两种方式

        // 方式1: 字符串（需要转义）
        CommandResult resultString = await _client.ExecuteCommandAsync(
            session.Id,
            "python -c \"print('Hello World')\"");

        // 方式2: 数组（不需要转义）
        CommandResult resultArray = await _client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print('Hello World')"]);

        // Assert - 两种方式结果应该相同
        _output.WriteLine($"String: {resultString.Stdout.Trim()}");
        _output.WriteLine($"Array: {resultArray.Stdout.Trim()}");
        
        Assert.Equal(resultString.Stdout.Trim(), resultArray.Stdout.Trim());
    }

    private string GetWorkPath(string relativePath)
    {
        relativePath = relativePath.TrimStart('\\', '/');
        var separator = _isWindowsContainer ? "\\" : "/";
        return _workDir.TrimEnd('\\', '/') + separator + relativePath.Replace("/", separator).Replace("\\", separator);
    }

    private static string EscapeForPythonRawString(string value)
    {
        // Python raw strings can't end with a single backslash; ensure we don't produce that pattern.
        // For our test paths, this is sufficient.
        return value.Replace("\"", "\\\"");
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
