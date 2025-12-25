using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 命令数组执行测试
/// 验证直接传递命令数组（不经过 shell 包装）
/// </summary>
[Collection(PythonCodePodCollection.Name)]
public class CommandArrayTests
{
    private readonly ITestOutputHelper _output;
    private readonly PythonCodePodFixture _fixture;

    private CodePodClient Client => _fixture.Client;
    private bool IsWindowsContainer => _fixture.IsWindowsContainer;
    private string WorkDir => _fixture.WorkDir;

    public CommandArrayTests(PythonCodePodFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task ExecuteCommandArray_BasicCommand_Works()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("命令数组基础测试");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        // Act - 使用命令数组
        CommandResult result = await Client.ExecuteCommandAsync(
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("避免转义测试");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        // Act - 包含特殊字符的参数，使用数组形式不需要转义
        CommandResult result = await Client.ExecuteCommandAsync(
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("Python 一行代码测试");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        // Act - AI 常用的 Python 一行代码执行
        CommandResult result = await Client.ExecuteCommandAsync(
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("Python 命令数组测试");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        // Act
        CommandResult result = await Client.ExecuteCommandAsync(
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("工作目录测试");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        string targetDir = GetWorkPath("testdir");
        await Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", $"import os; os.makedirs(r\"{EscapeForPythonRawString(targetDir)}\", exist_ok=True)"]);

        // Act - 在指定目录执行命令数组
        CommandResult result = await Client.ExecuteCommandAsync(
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("流式命令数组测试");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        // Act
        List<string> outputs = new();
        await foreach (CommandOutputEvent evt in Client.ExecuteCommandStreamAsync(
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("复杂 Python 代码测试");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        // 这是 AI 经常生成的多行 Python 代码
        var pythonCode = @"
import json
data = {'name': 'CodePod', 'version': '1.0', 'features': ['resource_limits', 'network_isolation']}
print(json.dumps(data, indent=2))
";

        // Act - 使用命令数组，代码中的引号和特殊字符不需要转义
        CommandResult result = await Client.ExecuteCommandAsync(
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync("字符串 vs 数组对比");
        await _fixture.WaitForSessionReadyAsync(session.Id);

        // 测试相同命令的两种方式

        // 方式1: 字符串（需要转义）
        CommandResult resultString = await Client.ExecuteCommandAsync(
            session.Id,
            "python -c \"print('Hello World')\"");

        // 方式2: 数组（不需要转义）
        CommandResult resultArray = await Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print('Hello World')"]);

        // Assert - 两种方式结果应该相同
        _output.WriteLine($"String: {resultString.Stdout.Trim()}");
        _output.WriteLine($"Array: {resultArray.Stdout.Trim()}");
        
        Assert.Equal(resultString.Stdout.Trim(), resultArray.Stdout.Trim());
    }

    private string GetWorkPath(string relativePath) =>
        _fixture.GetWorkPath(relativePath);

    private static string EscapeForPythonRawString(string value)
    {
        // Python raw strings can't end with a single backslash; ensure we don't produce that pattern.
        // For our test paths, this is sufficient.
        return value.Replace("\"", "\\\"");
    }
}
