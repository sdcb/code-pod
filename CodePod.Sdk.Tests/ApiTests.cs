using System.Text;
using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 综合 API 测试基类 - 提供测试上下文创建辅助方法
/// </summary>
public abstract class ApiTestBase
{
    /// <summary>
    /// 获取有意义的 Label Prefix，用于区分不同的测试
    /// 默认使用类名作为标识
    /// </summary>
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    /// <summary>
    /// 创建测试上下文
    /// </summary>
    protected Task<TestClientContext> CreateContextAsync(Action<CodePodConfig>? configure = null)
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.LabelPrefix = GetLabelPrefix();
            configure?.Invoke(config);
        });
    }
}

public class System_GetStatusTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task GetStatus_ReturnsValidStatus()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Act
        SystemStatus status = await context.Client.GetStatusAsync();

        // Assert
        Assert.True(status.MaxContainers > 0);
        Assert.True(status.AvailableContainers >= 0);
    }
}

public class System_GetAllContainersTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task GetAllContainers_ReturnsList()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Act
        List<ContainerInfo> containers = await context.Client.GetAllContainersAsync();

        // Assert
        Assert.NotNull(containers);
    }
}

public class System_PrewarmLogicTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task PrewarmLogic_CreatesContainers()
    {
        await using TestClientContext context = await CreateContextAsync(config =>
        {
            config.PrewarmCount = 1;
        });

        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SystemStatus beforeStatus = await context.Client.GetStatusAsync();

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "补充测试" });
        await Task.Delay(1000); // 等待预热逻辑生效
        SystemStatus afterStatus = await context.Client.GetStatusAsync();

        // Assert
        int totalAfter = afterStatus.AvailableContainers + afterStatus.WarmingContainers;
        Assert.True(totalAfter >= 1, "Should have containers available or warming");
    }
}

public class Session_CreateTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task CreateSession_ReturnsValidSession()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "API测试会话" });

        // Assert
        Assert.NotNull(session);
        Assert.True(session.Id > 0);
        Assert.Equal("API测试会话", session.Name);
    }
}

public class Session_GetTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task GetSession_AfterCreate_ReturnsCorrectDetails()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "详情测试" });

        // Act
        SessionInfo retrieved = await context.Client.GetSessionAsync(session.Id);

        // Assert
        Assert.Equal(session.Id, retrieved.Id);
        Assert.NotNull(retrieved.ContainerId);
    }
}

public class Session_GetAllTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task GetAllSessions_ReturnsList()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessionTracker = new(context.Client);

        // Arrange
        await sessionTracker.CreateSessionAsync(new SessionOptions { Name = "会话1" });
        await sessionTracker.CreateSessionAsync(new SessionOptions { Name = "会话2" });

        // Act
        IReadOnlyList<SessionInfo> sessions = await context.Client.GetAllSessionsAsync();

        // Assert
        Assert.True(sessions.Count >= 2);
    }
}

public class Session_DestroyTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task DestroySession_RemovesSession()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessionTracker = new(context.Client);

        // Arrange
        SessionInfo session = await sessionTracker.CreateSessionAsync(new SessionOptions { Name = "销毁测试" });

        // Act
        await context.Client.DestroySessionAsync(session.Id);
        await Task.Delay(1000); // 等待清理完成

        // Assert
        IReadOnlyList<SessionInfo> sessions = await context.Client.GetAllSessionsAsync();
        Assert.DoesNotContain(sessions, s => s.Id == session.Id);
    }
}

public class Session_CustomTimeoutTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task SessionCustomTimeout_IsRespected()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "短超时测试", TimeoutSeconds = 10 });

        // Assert
        Assert.Equal(10, session.TimeoutSeconds);
    }
}

public class Session_TimeoutExceedsLimitTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task SessionTimeout_ExceedsLimit_ThrowsException()
    {
        await using TestClientContext context = await CreateContextAsync();

        // Arrange & Act & Assert
        await Assert.ThrowsAsync<Exceptions.TimeoutExceedsLimitException>(() =>
            context.Client.CreateSessionAsync(new SessionOptions { Name = "超长超时测试", TimeoutSeconds = 999999 }));
    }
}

public class Command_BasicExecuteTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task ExecuteCommand_BasicOutput_ReturnsCorrectResult()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "命令测试" });

        // Act
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "echo 'Hello from CodePod SDK!'");

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from CodePod SDK!", result.Stdout);
    }
}

public class Command_ErrorExecuteTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task ExecuteCommand_WithError_ReturnsNonZeroExitCode()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "错误命令测试" });

        // Act
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            "nonexistent_command_12345");

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
    }
}

public class Command_MultiLineExecuteTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task ExecuteCommand_MultiLineOutput_ReturnsAllLines()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "多行输出测试" });

        // Act - 使用跨平台命令
        CommandResult result = await context.Client.ExecuteCommandAsync(
            session.Id,
            context.GetMultiLineEchoCommand(3));

        // Assert
        Assert.Equal(0, result.ExitCode);
        string[] lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }
}

public class Command_StreamExecuteTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task ExecuteCommandStream_ReturnsStreamedOutput()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "流式输出测试" });

        // Act - 使用跨平台命令
        List<string> stdoutEvents = new();
        List<string> stderrEvents = new();
        long? exitCode = null;

        await foreach (CommandOutputEvent evt in context.Client.ExecuteCommandStreamAsync(
            session.Id,
            context.GetStreamingOutputCommand(3, 0.1)))
        {
            switch (evt.Type)
            {
                case Models.CommandOutputType.Stdout:
                    stdoutEvents.Add(evt.Data ?? "");
                    break;
                case Models.CommandOutputType.Stderr:
                    stderrEvents.Add(evt.Data ?? "");
                    break;
                case Models.CommandOutputType.Exit:
                    exitCode = evt.ExitCode;
                    break;
            }
        }

        // Assert
        Assert.True(stdoutEvents.Count > 0, "Should receive stdout events");
        Assert.True(stderrEvents.Count > 0, "Should receive stderr events");
        Assert.NotNull(exitCode);
        Assert.Equal(0, exitCode);
    }
}

public class File_UploadTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task UploadFile_Succeeds()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "上传文件测试" });
        string content = "Hello, this is a test file!\n测试中文内容";
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        // Act
        await context.Client.UploadFileAsync(session.Id, context.GetWorkPath("test.txt"), bytes);

        // Assert - 验证文件存在
        List<FileEntry> files = await context.Client.ListDirectoryAsync(session.Id, context.WorkDir);
        Assert.Contains(files, f => f.Name == "test.txt");
    }
}

public class File_ListDirectoryTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task ListDirectory_ReturnsFiles()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "列目录测试" });
        string content = "test content";
        await context.Client.UploadFileAsync(session.Id, context.GetWorkPath("listtest.txt"), Encoding.UTF8.GetBytes(content));

        // Act
        List<FileEntry> files = await context.Client.ListDirectoryAsync(session.Id, context.WorkDir);

        // Assert
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Name == "listtest.txt");
    }
}

public class File_DownloadTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task DownloadFile_ReturnsCorrectContent()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "下载文件测试" });
        string originalContent = "Hello, this is a test file!\n测试中文内容";
        await context.Client.UploadFileAsync(session.Id, context.GetWorkPath("download.txt"), Encoding.UTF8.GetBytes(originalContent));

        // Act
        byte[] downloadedBytes = await context.Client.DownloadFileAsync(session.Id, context.GetWorkPath("download.txt"));
        string downloadedContent = Encoding.UTF8.GetString(downloadedBytes);

        // Assert
        Assert.Contains("Hello", downloadedContent);
        Assert.Contains("测试中文内容", downloadedContent);
    }
}

public class File_DeleteTest : ApiTestBase
{
    [Fact]
    [Trait("Category", "API")]
    public async Task DeleteFile_RemovesFile()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "删除文件测试" });
        await context.Client.UploadFileAsync(session.Id, context.GetWorkPath("todelete.txt"), Encoding.UTF8.GetBytes("delete me"));

        // Act
        await context.Client.DeleteFileAsync(session.Id, context.GetWorkPath("todelete.txt"));

        // Assert
        List<FileEntry> files = await context.Client.ListDirectoryAsync(session.Id, context.WorkDir);
        Assert.DoesNotContain(files, f => f.Name == "todelete.txt");
    }
}
