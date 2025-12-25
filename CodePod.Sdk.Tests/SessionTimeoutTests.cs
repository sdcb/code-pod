using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 会话超时测试 - 对应 test/SessionTimeoutTest.cs
/// </summary>
[Collection(SessionTimeoutCollection.Name)]
public class SessionTimeoutTests
{
    private readonly SessionTimeoutCodePodFixture _fixture;

    private CodePodClient Client => _fixture.Client;
    private CodePodConfig Config => _fixture.Config;
    private Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => _fixture.LoggerFactory;
    private string WorkDir => _fixture.WorkDir;

    public SessionTimeoutTests(SessionTimeoutCodePodFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CommandExecution_ExtendsTimeout()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "命令延时测试", TimeoutSeconds = 10 });
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "上传延时测试", TimeoutSeconds = 10 });
        DateTimeOffset initialLastActivity = session.LastActivityAt;

        await Task.Delay(1000);

        // Act
        await Client.UploadFileAsync(session.Id, _fixture.GetWorkPath("timeout-test.txt"), "Hello, World!"u8.ToArray());

        // Assert
        SessionInfo updatedSession = await Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }

    [Fact]
    public async Task ListDirectory_ExtendsTimeout()
    {
        // Arrange
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "列目录延时测试", TimeoutSeconds = 10 });
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
        await using TestSessionTracker sessions = new(Client);
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "下载延时测试", TimeoutSeconds = 10 });

        // 先上传一个文件
        string downloadPath = _fixture.GetWorkPath("download-timeout.txt");
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
        bool isWindowsContainer = settings.IsWindowsContainer;

        CodePodConfig shortTimeoutConfig = new()
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
            SessionInfo session = await shortTimeoutClient.CreateSessionAsync(new SessionOptions { Name = "超时销毁测试", TimeoutSeconds = 2 });

            string containerId = session.ContainerId;
            
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
