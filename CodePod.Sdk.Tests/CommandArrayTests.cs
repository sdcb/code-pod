using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 命令数组执行测试基类
/// 验证直接传递命令数组（不经过 shell 包装）
/// </summary>
public abstract class CommandArrayTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            CodePodTestSettings settings = TestSettings.Load();
            config.Image = settings.IsWindowsContainer ? settings.PythonWindowsImage : settings.PythonLinuxImage;
            config.SessionTimeoutSeconds = 300;
            config.LabelPrefix = GetLabelPrefix();
        });
    }

    protected static string EscapeForPythonRawString(string value)
    {
        return value.Replace("\"", "\\\"");
    }
}

public class CommandArray_BasicTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task ExecuteCommandArray_BasicCommand_Works()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "命令数组基础测试" });

        // Act - 使用命令数组
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print('Hello from command array!')"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from command array!", result.Stdout);
    }
}

public class CommandArray_ShellEscapingTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task ExecuteCommandArray_AvoidShellEscaping()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "避免转义测试" });

        // Act - 包含特殊字符的参数，使用数组形式不需要转义
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print(\"Hello \\\"World\\\" with 'quotes' and $variables\")"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"World\"", result.Stdout);
        Assert.Contains("'quotes'", result.Stdout);
        Assert.Contains("$variables", result.Stdout);
    }
}

public class CommandArray_PythonOnelinerTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task ExecuteCommandArray_PythonOneliner()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "Python 一行代码测试" });

        // Act - AI 常用的 Python 一行代码执行
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print('Hello from Python!\\nLine 2\\nLine 3')"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from Python!", result.Stdout);
    }
}

public class CommandArray_PythonVersionTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task ExecuteCommandArray_PythonVersion()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "Python 命令数组测试" });

        // Act
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "--version"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"Python \d+\.\d+", result.Stdout + result.Stderr);
    }
}

public class CommandArray_WorkingDirectoryTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task ExecuteCommandArray_WithWorkingDirectory()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "工作目录测试" });

        string targetDir = context.GetWorkPath("testdir");
        await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", $"import os; os.makedirs(r\"{EscapeForPythonRawString(targetDir)}\", exist_ok=True)"]);

        // Act - 在指定目录执行命令数组
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "import os; print(os.getcwd())"],
            workingDirectory: targetDir);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("testdir", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }
}

public class CommandArray_StreamTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task ExecuteCommandArrayStream_Works()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "流式命令数组测试" });

        // Act
        List<string> outputs = new();
        await foreach (CommandOutputEvent evt in context.Client.ExecuteCommandStreamAsync(
            session.Id,
            ["python", "-c", "for i in range(1,4): print(f'Line{i}')"]))
        {
            if (evt.Type == CommandOutputType.Stdout)
            {
                outputs.Add(evt.Data ?? "");
            }
        }

        // Assert
        string allOutput = string.Join("", outputs);
        Assert.Contains("Line1", allOutput);
        Assert.Contains("Line2", allOutput);
        Assert.Contains("Line3", allOutput);
    }
}

public class CommandArray_ComplexPythonCodeTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task ExecuteCommandArray_ComplexPythonCode()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "复杂 Python 代码测试" });

        string pythonCode = @"
import json
data = {'name': 'CodePod', 'version': '1.0', 'features': ['resource_limits', 'network_isolation']}
print(json.dumps(data, indent=2))
";

        // Act - 使用命令数组，代码中的引号和特殊字符不需要转义
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", pythonCode]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CodePod", result.Stdout);
        Assert.Contains("resource_limits", result.Stdout);
    }
}

public class CommandArray_ComparisonTest : CommandArrayTestBase
{
    [Fact]
    [Trait("Category", "CommandArray")]
    public async Task CommandString_VsCommandArray_Comparison()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "字符串 vs 数组对比" });

        // Act - 方式1: 字符串（需要转义）
        CommandResult resultString = await context.Client.ExecuteCommandAsync(
            session.Id,
            "python -c \"print('Hello World')\"");

        // 方式2: 数组（不需要转义）
        CommandResult resultArray = await context.Client.ExecuteCommandAsync(
            session.Id,
            ["python", "-c", "print('Hello World')"]);

        // Assert - 两种方式结果应该相同
        Assert.Equal(resultString.Stdout.Trim(), resultArray.Stdout.Trim());
    }
}
