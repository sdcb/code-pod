using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 会话超时测试基类
/// </summary>
public abstract class SessionTimeoutTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.PrewarmCount = 0;
            config.SessionTimeoutSeconds = 10;
            config.LabelPrefix = GetLabelPrefix();
        });
    }
}

public class SessionTimeout_CommandExtensionTest : SessionTimeoutTestBase
{
    [Fact]
    [Trait("Category", "SessionTimeout")]
    public async Task CommandExecution_ExtendsTimeout()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "命令延时测试", TimeoutSeconds = 10 });
        DateTimeOffset initialLastActivity = session.LastActivityAt;

        await Task.Delay(1000);

        // Act - 执行命令
        await context.Client.ExecuteCommandAsync(session.Id, "echo 'hello'");

        // Assert
        SessionInfo updatedSession = await context.Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }
}

public class SessionTimeout_UploadExtensionTest : SessionTimeoutTestBase
{
    [Fact]
    [Trait("Category", "SessionTimeout")]
    public async Task FileUpload_ExtendsTimeout()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "上传延时测试", TimeoutSeconds = 10 });
        DateTimeOffset initialLastActivity = session.LastActivityAt;

        await Task.Delay(1000);

        // Act
        await context.Client.UploadFileAsync(session.Id, context.GetWorkPath("timeout-test.txt"), "Hello, World!"u8.ToArray());

        // Assert
        SessionInfo updatedSession = await context.Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }
}

public class SessionTimeout_ListExtensionTest : SessionTimeoutTestBase
{
    [Fact]
    [Trait("Category", "SessionTimeout")]
    public async Task ListDirectory_ExtendsTimeout()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "列目录延时测试", TimeoutSeconds = 10 });
        DateTimeOffset initialLastActivity = session.LastActivityAt;

        await Task.Delay(1000);

        // Act
        await context.Client.ListDirectoryAsync(session.Id, context.WorkDir);

        // Assert
        SessionInfo updatedSession = await context.Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }
}

public class SessionTimeout_DownloadExtensionTest : SessionTimeoutTestBase
{
    [Fact]
    [Trait("Category", "SessionTimeout")]
    public async Task FileDownload_ExtendsTimeout()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "下载延时测试", TimeoutSeconds = 10 });

        // 先上传一个文件
        string downloadPath = context.GetWorkPath("download-timeout.txt");
        await context.Client.UploadFileAsync(session.Id, downloadPath, "test content"u8.ToArray());

        DateTimeOffset initialLastActivity = (await context.Client.GetSessionAsync(session.Id)).LastActivityAt;
        await Task.Delay(1000);

        // Act
        await context.Client.DownloadFileAsync(session.Id, downloadPath);

        // Assert
        SessionInfo updatedSession = await context.Client.GetSessionAsync(session.Id);
        Assert.True(updatedSession.LastActivityAt > initialLastActivity);
    }
}

public class SessionTimeout_DestroyTest : SessionTimeoutTestBase
{
    [Fact]
    [Trait("Category", "SessionTimeout")]
    public async Task SessionTimeout_DestroysSessionAndContainer()
    {
        // 使用非常短的超时来测试
        CodePodTestSettings settings = TestSettings.Load();

        await using TestClientContext shortContext = await TestClientContext.CreateAsync(config =>
        {
            config.PrewarmCount = 0;
            config.MaxContainers = 5;
            config.SessionTimeoutSeconds = 5;
            config.LabelPrefix = $"codepod-timeout-test-{Guid.NewGuid():N}";
        });

        CodePodClient shortTimeoutClient = shortContext.Client;

        // Arrange
        SessionInfo session = await shortTimeoutClient.CreateSessionAsync(new SessionOptions { Name = "超时销毁测试", TimeoutSeconds = 2 });

        // 等待超时
        await Task.Delay(4000);

        // Act - 触发清理
        await shortTimeoutClient.CleanupExpiredSessionsAsync();
        await Task.Delay(1000); // 等待清理完成

        // Assert
        IReadOnlyList<SessionInfo> sessions = await shortTimeoutClient.GetAllSessionsAsync();
        Assert.DoesNotContain(sessions, s => s.Id == session.Id);
    }
}
