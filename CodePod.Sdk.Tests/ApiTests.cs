using System.Text;
using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 综合 API 测试 - 对应 test/DockerApiTest.cs
/// </summary>
[Collection(CodePodCollection.Name)]
[TestCaseOrderer("CodePod.Sdk.Tests.TestInfrastructure.PriorityOrderer", "CodePod.Sdk.Tests")]
public class ApiTests
{
    private readonly CodePodFixture _fixture;

    private CodePodClient Client => _fixture.Client;
    private string WorkDir => _fixture.WorkDir;

    public ApiTests(CodePodFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [TestPriority(0)]
    public async Task GetStatus_ReturnsValidStatus()
    {
        // Act
        SystemStatus status = await Client.GetStatusAsync();

        // Assert
        Assert.True(status.MaxContainers > 0);
        Assert.True(status.AvailableContainers >= 0);
    }

    [Fact]
    [TestPriority(1)]
    public async Task CreateSession_ReturnsValidSession()
    {
        await using TestSessionTracker sessions = new(Client);

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "API测试会话" });

        // Assert
        Assert.NotNull(session);
        Assert.True(session.Id > 0);
        Assert.Equal("API测试会话", session.Name);
    }

    [Fact]
    [TestPriority(1)]
    public async Task GetSession_AfterCreate_ReturnsCorrectDetails()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "详情测试" });

        // Act
        SessionInfo retrieved = await Client.GetSessionAsync(session.Id);

        // Assert
        Assert.Equal(session.Id, retrieved.Id);
        Assert.NotNull(retrieved.ContainerId);
    }

    [Fact]
    [TestPriority(1)]
    public async Task ExecuteCommand_BasicOutput_ReturnsCorrectResult()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "命令测试" });

        // Act
        CommandResult result = await Client.ExecuteCommandAsync(
            session.Id,
            "echo 'Hello from CodePod SDK!'");

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from CodePod SDK!", result.Stdout);
    }

    [Fact]
    [TestPriority(1)]
    public async Task ExecuteCommand_WithError_ReturnsNonZeroExitCode()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "错误命令测试" });

        // Act
        CommandResult result = await Client.ExecuteCommandAsync(
            session.Id,
            "nonexistent_command_12345");

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
    }

    [Fact]
    [TestPriority(1)]
    public async Task ExecuteCommand_MultiLineOutput_ReturnsAllLines()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "多行输出测试" });

        // Act - 使用跨平台命令
        CommandResult result = await Client.ExecuteCommandAsync(
            session.Id,
            _fixture.GetMultiLineEchoCommand(3));

        // Assert
        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    [TestPriority(1)]
    public async Task ExecuteCommandStream_ReturnsStreamedOutput()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "流式输出测试" });

        // Act - 使用跨平台命令
        List<string> stdoutEvents = new();
        List<string> stderrEvents = new();
        long? exitCode = null;

        await foreach (CommandOutputEvent evt in Client.ExecuteCommandStreamAsync(
            session.Id,
            _fixture.GetStreamingOutputCommand(3, 0.1)))
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

    [Fact]
    [TestPriority(1)]
    public async Task UploadFile_Succeeds()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "上传文件测试" });
        var content = "Hello, this is a test file!\n测试中文内容";
        var bytes = Encoding.UTF8.GetBytes(content);

        // Act
        await Client.UploadFileAsync(session.Id, _fixture.GetWorkPath("test.txt"), bytes);

        // Assert - 验证文件存在
        List<FileEntry> files = await Client.ListDirectoryAsync(session.Id, WorkDir);
        Assert.Contains(files, f => f.Name == "test.txt");
    }

    [Fact]
    [TestPriority(1)]
    public async Task ListDirectory_ReturnsFiles()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "列目录测试" });
        var content = "test content";
        await Client.UploadFileAsync(session.Id, _fixture.GetWorkPath("listtest.txt"), Encoding.UTF8.GetBytes(content));

        // Act
        List<FileEntry> files = await Client.ListDirectoryAsync(session.Id, WorkDir);

        // Assert
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Name == "listtest.txt");
    }

    [Fact]
    [TestPriority(1)]
    public async Task DownloadFile_ReturnsCorrectContent()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "下载文件测试" });
        var originalContent = "Hello, this is a test file!\n测试中文内容";
        await Client.UploadFileAsync(session.Id, _fixture.GetWorkPath("download.txt"), Encoding.UTF8.GetBytes(originalContent));

        // Act
        var downloadedBytes = await Client.DownloadFileAsync(session.Id, _fixture.GetWorkPath("download.txt"));
        var downloadedContent = Encoding.UTF8.GetString(downloadedBytes);

        // Assert
        Assert.Contains("Hello", downloadedContent);
        Assert.Contains("测试中文内容", downloadedContent);
    }

    [Fact]
    [TestPriority(1)]
    public async Task DeleteFile_RemovesFile()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "删除文件测试" });
        await Client.UploadFileAsync(session.Id, _fixture.GetWorkPath("todelete.txt"), Encoding.UTF8.GetBytes("delete me"));

        // Act
        await Client.DeleteFileAsync(session.Id, _fixture.GetWorkPath("todelete.txt"));

        // Assert
        List<FileEntry> files = await Client.ListDirectoryAsync(session.Id, WorkDir);
        Assert.DoesNotContain(files, f => f.Name == "todelete.txt");
    }

    [Fact]
    [TestPriority(1)]
    public async Task GetAllSessions_ReturnsList()
    {
        await using TestSessionTracker sessionTracker = new(Client);

        // Arrange
        await sessionTracker.CreateSessionAsync(new SessionOptions { Name = "会话1" });
        await sessionTracker.CreateSessionAsync(new SessionOptions { Name = "会话2" });

        // Act
        IReadOnlyList<SessionInfo> sessions = await Client.GetAllSessionsAsync();

        // Assert
        Assert.True(sessions.Count >= 2);
    }

    [Fact]
    [TestPriority(1)]
    public async Task GetAllContainers_ReturnsList()
    {
        // Act
        List<ContainerInfo> containers = await Client.GetAllContainersAsync();

        // Assert
        Assert.NotNull(containers);
    }

    [Fact]
    [TestPriority(2)]
    public async Task DestroySession_RemovesSession()
    {
        await using TestSessionTracker sessionTracker = new(Client);

        // Arrange
        SessionInfo session = await sessionTracker.CreateSessionAsync(new SessionOptions { Name = "销毁测试" });

        // Act
        await Client.DestroySessionAsync(session.Id);
        await Task.Delay(1000); // 等待清理完成

        // Assert
        IReadOnlyList<SessionInfo> sessions = await Client.GetAllSessionsAsync();
        Assert.DoesNotContain(sessions, s => s.Id == session.Id);
    }

    [Fact]
    [TestPriority(1)]
    public async Task SessionCustomTimeout_IsRespected()
    {
        await using TestSessionTracker sessions = new(Client);

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "短超时测试", TimeoutSeconds = 10 });

        // Assert
        Assert.Equal(10, session.TimeoutSeconds);
    }

    [Fact]
    [TestPriority(1)]
    public async Task SessionTimeout_ExceedsLimit_ThrowsException()
    {
        // Arrange & Act & Assert
        await Assert.ThrowsAsync<Exceptions.TimeoutExceedsLimitException>(() =>
            Client.CreateSessionAsync(new SessionOptions { Name = "超长超时测试", TimeoutSeconds = 999999 }));
    }

    [Fact]
    [TestPriority(2)]
    public async Task PrewarmLogic_CreatesContainers()
    {
        await using TestSessionTracker sessions = new(Client);

        // Arrange
        SystemStatus beforeStatus = await Client.GetStatusAsync();

        // Act
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "补充测试" });
        
        // 等待预热容器创建
        await Task.Delay(2000);
        SystemStatus afterStatus = await Client.GetStatusAsync();

        // Assert
        var totalAfter = afterStatus.AvailableContainers + afterStatus.WarmingContainers;
        Assert.True(totalAfter >= 1, "Should have containers available or warming");

    }
}
