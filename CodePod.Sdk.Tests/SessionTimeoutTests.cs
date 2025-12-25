using CodePod.Sdk.Models;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 会话超时测试 - 对应 test/SessionTimeoutTest.cs
/// </summary>
public class SessionTimeoutTests : TestBase
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        // 为超时测试使用较短的超时时间
        Config.SessionTimeoutSeconds = 60;
    }

    [Fact]
    public async Task CommandExecution_ExtendsTimeout()
    {
        // Arrange
        SessionInfo session = await Client.CreateSessionAsync("命令延时测试", 10);
        await WaitForSessionReadyAsync(session.Id);
        DateTimeOffset initialLastActivity = session.LastActivityAt;

        // Wait a bit
        await Task.Delay(1000);

        // Act - 执行命令
        await Client.ExecuteCommandAsync(session.Id, "echo 'hello'");

        // Assert
        SessionInfo updatedSession = await Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }

    [Fact]
    public async Task FileUpload_ExtendsTimeout()
    {
        // Arrange
        SessionInfo session = await Client.CreateSessionAsync("上传延时测试", 10);
        await WaitForSessionReadyAsync(session.Id);
        DateTimeOffset initialLastActivity = session.LastActivityAt;

        await Task.Delay(1000);

        // Act
        await Client.UploadFileAsync(session.Id, GetWorkPath("timeout-test.txt"), "Hello, World!"u8.ToArray());

        // Assert
        SessionInfo updatedSession = await Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }

    [Fact]
    public async Task ListDirectory_ExtendsTimeout()
    {
        // Arrange
        SessionInfo session = await Client.CreateSessionAsync("列目录延时测试", 10);
        await WaitForSessionReadyAsync(session.Id);
        DateTimeOffset initialLastActivity = session.LastActivityAt;

        await Task.Delay(1000);

        // Act
        await Client.ListDirectoryAsync(session.Id, WorkDir);

        // Assert
        SessionInfo updatedSession = await Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }

    [Fact]
    public async Task FileDownload_ExtendsTimeout()
    {
        // Arrange
        SessionInfo session = await Client.CreateSessionAsync("下载延时测试", 10);
        await WaitForSessionReadyAsync(session.Id);
        
        // 先上传一个文件
        var downloadPath = GetWorkPath("download-timeout.txt");
        await Client.UploadFileAsync(session.Id, downloadPath, "test content"u8.ToArray());

        DateTimeOffset initialLastActivity = (await Client.GetSessionAsync(session.Id)).LastActivityAt;
        await Task.Delay(1000);

        // Act
        await Client.DownloadFileAsync(session.Id, downloadPath);

        // Assert
        SessionInfo updatedSession = await Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }

    [Fact]
    public async Task SessionTimeout_DestroysSessionAndContainer()
    {
        // 使用非常短的超时来测试
        CodePodTestSettings settings = TestSettings.Load();
        var isWindowsContainer = settings.IsWindowsContainer;

        var shortTimeoutConfig = new Configuration.CodePodConfig
        {
            IsWindowsContainer = isWindowsContainer,
            DockerEndpoint = settings.DockerEndpoint,
            Image = Config.Image,
            PrewarmCount = 1,
            MaxContainers = 5,
            SessionTimeoutSeconds = 5, // 5秒超时
            WorkDir = isWindowsContainer ? "C:\\app" : "/app",
            LabelPrefix = "codepod-timeout-test"
        };

        using CodePodClient shortTimeoutClient = new CodePodClientBuilder()
            .WithConfig(shortTimeoutConfig)
            .WithLogging(LoggerFactory)
            .Build();

        await shortTimeoutClient.InitializeAsync();

        try
        {
            // Arrange
            SessionInfo session = await shortTimeoutClient.CreateSessionAsync("超时销毁测试", 2);
            await Task.Delay(1000); // 等待容器分配
            
            var containerId = session.ContainerId;
            
            // 等待超时
            await Task.Delay(4000);

            // Act - 触发清理
            await shortTimeoutClient.CleanupExpiredSessionsAsync();
            await Task.Delay(1000); // 等待清理完成

            // Assert
            IReadOnlyList<SessionInfo> sessions = await shortTimeoutClient.GetAllSessionsAsync();
            Assert.DoesNotContain(sessions, s => s.Id == session.Id);
        }
        finally
        {
            await shortTimeoutClient.DeleteAllContainersAsync();
        }
    }
}
