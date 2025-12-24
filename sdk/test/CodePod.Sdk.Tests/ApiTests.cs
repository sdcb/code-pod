using System.Text;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 综合 API 测试 - 对应 test/DockerApiTest.cs
/// </summary>
public class ApiTests : TestBase
{
    [Fact]
    public async Task GetStatus_ReturnsValidStatus()
    {
        // Act
        var status = await Client.GetStatusAsync();

        // Assert
        Assert.True(status.MaxContainers > 0);
        Assert.True(status.AvailableContainers >= 0);
    }

    [Fact]
    public async Task CreateSession_ReturnsValidSession()
    {
        // Act
        var session = await Client.CreateSessionAsync("API测试会话");

        // Assert
        Assert.NotNull(session);
        Assert.True(session.Id > 0);
        Assert.Equal("API测试会话", session.Name);
    }

    [Fact]
    public async Task GetSession_AfterCreate_ReturnsCorrectDetails()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("详情测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act
        var retrieved = await Client.GetSessionAsync(session.Id);

        // Assert
        Assert.Equal(session.Id, retrieved.Id);
        Assert.NotNull(retrieved.ContainerId);
    }

    [Fact]
    public async Task ExecuteCommand_BasicOutput_ReturnsCorrectResult()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("命令测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act
        var result = await Client.ExecuteCommandAsync(
            session.Id,
            "echo 'Hello from CodePod SDK!'");

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello from CodePod SDK!", result.Stdout);
    }

    [Fact]
    public async Task ExecuteCommand_WithError_ReturnsNonZeroExitCode()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("错误命令测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act
        var result = await Client.ExecuteCommandAsync(
            session.Id,
            "nonexistent_command_12345");

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
    }

    [Fact]
    public async Task ExecuteCommand_MultiLineOutput_ReturnsAllLines()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("多行输出测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act - 使用跨平台命令
        var result = await Client.ExecuteCommandAsync(
            session.Id,
            GetMultiLineEchoCommand(3));

        // Assert
        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task ExecuteCommandStream_ReturnsStreamedOutput()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("流式输出测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act - 使用跨平台命令
        var stdoutEvents = new List<string>();
        var stderrEvents = new List<string>();
        long? exitCode = null;

        await foreach (var evt in Client.ExecuteCommandStreamAsync(
            session.Id,
            GetStreamingOutputCommand(3, 0.1)))
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
    public async Task UploadFile_Succeeds()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("上传文件测试");
        await WaitForSessionReadyAsync(session.Id);
        var content = "Hello, this is a test file!\n测试中文内容";
        var bytes = Encoding.UTF8.GetBytes(content);

        // Act
        await Client.UploadFileAsync(session.Id, GetWorkPath("test.txt"), bytes);

        // Assert - 验证文件存在
        var files = await Client.ListDirectoryAsync(session.Id, WorkDir);
        Assert.Contains(files, f => f.Name == "test.txt");
    }

    [Fact]
    public async Task ListDirectory_ReturnsFiles()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("列目录测试");
        await WaitForSessionReadyAsync(session.Id);
        var content = "test content";
        await Client.UploadFileAsync(session.Id, GetWorkPath("listtest.txt"), Encoding.UTF8.GetBytes(content));

        // Act
        var files = await Client.ListDirectoryAsync(session.Id, WorkDir);

        // Assert
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Name == "listtest.txt");
    }

    [Fact]
    public async Task DownloadFile_ReturnsCorrectContent()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("下载文件测试");
        await WaitForSessionReadyAsync(session.Id);
        var originalContent = "Hello, this is a test file!\n测试中文内容";
        await Client.UploadFileAsync(session.Id, GetWorkPath("download.txt"), Encoding.UTF8.GetBytes(originalContent));

        // Act
        var downloadedBytes = await Client.DownloadFileAsync(session.Id, GetWorkPath("download.txt"));
        var downloadedContent = Encoding.UTF8.GetString(downloadedBytes);

        // Assert
        Assert.Contains("Hello", downloadedContent);
        Assert.Contains("测试中文内容", downloadedContent);
    }

    [Fact]
    public async Task DeleteFile_RemovesFile()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("删除文件测试");
        await WaitForSessionReadyAsync(session.Id);
        await Client.UploadFileAsync(session.Id, GetWorkPath("todelete.txt"), Encoding.UTF8.GetBytes("delete me"));

        // Act
        await Client.DeleteFileAsync(session.Id, GetWorkPath("todelete.txt"));

        // Assert
        var files = await Client.ListDirectoryAsync(session.Id, WorkDir);
        Assert.DoesNotContain(files, f => f.Name == "todelete.txt");
    }

    [Fact]
    public async Task GetAllSessions_ReturnsList()
    {
        // Arrange
        await Client.CreateSessionAsync("会话1");
        await Client.CreateSessionAsync("会话2");

        // Act
        var sessions = await Client.GetAllSessionsAsync();

        // Assert
        Assert.True(sessions.Count >= 2);
    }

    [Fact]
    public async Task GetAllContainers_ReturnsList()
    {
        // Act
        var containers = await Client.GetAllContainersAsync();

        // Assert
        Assert.NotNull(containers);
    }

    [Fact]
    public async Task DestroySession_RemovesSession()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("销毁测试");
        await WaitForSessionReadyAsync(session.Id);

        // Act
        await Client.DestroySessionAsync(session.Id);
        await Task.Delay(1000); // 等待清理完成

        // Assert
        var sessions = await Client.GetAllSessionsAsync();
        Assert.DoesNotContain(sessions, s => s.Id == session.Id);
    }

    [Fact]
    public async Task SessionCustomTimeout_IsRespected()
    {
        // Act
        var session = await Client.CreateSessionAsync("短超时测试", 10);

        // Assert
        Assert.Equal(10, session.TimeoutSeconds);
    }

    [Fact]
    public async Task SessionTimeout_ExceedsLimit_ThrowsException()
    {
        // Arrange & Act & Assert
        await Assert.ThrowsAsync<Exceptions.TimeoutExceedsLimitException>(() =>
            Client.CreateSessionAsync("超长超时测试", 999999));
    }

    [Fact]
    public async Task PrewarmLogic_CreatesContainers()
    {
        // Arrange
        var beforeStatus = await Client.GetStatusAsync();
        
        // Act
        var session = await Client.CreateSessionAsync("补充测试");
        await WaitForSessionReadyAsync(session.Id);
        
        // 等待预热容器创建
        await Task.Delay(2000);
        var afterStatus = await Client.GetStatusAsync();

        // Assert
        var totalAfter = afterStatus.AvailableContainers + afterStatus.WarmingContainers;
        Assert.True(totalAfter >= 1, "Should have containers available or warming");

        // Cleanup
        await Client.DestroySessionAsync(session.Id);
    }
}
