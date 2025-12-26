using CodePod.Sdk.Models;
using System.Diagnostics;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 性能测试基类
/// </summary>
public abstract class PerformanceTestBase
{
    protected virtual string GetLabelPrefix() => $"codepod-test-{GetType().Name.ToLowerInvariant()}";

    protected Task<TestClientContext> CreateContextAsync()
    {
        return TestClientContext.CreateAsync(config =>
        {
            config.LabelPrefix = GetLabelPrefix();
        });
    }
}

public class Performance_SessionCreationTest : PerformanceTestBase
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task SessionCreation_Performance()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        const int TestRounds = 3;
        List<double> sessionCreateTimes = new();

        for (int round = 1; round <= TestRounds; round++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = $"性能测试-{round}" });
            sw.Stop();
            sessionCreateTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        // Assert - 会话创建时间应在合理范围内
        double avgCreateTime = sessionCreateTimes.Average();
        Assert.True(avgCreateTime < 30000, $"Average session creation time ({avgCreateTime}ms) should be under 30 seconds");
    }
}

public class Performance_CommandExecutionTest : PerformanceTestBase
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task CommandExecution_Performance()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = "命令性能测试" });
        List<double> commandExecTimes = new();

        for (int i = 0; i < 5; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            CommandResult result = await context.Client.ExecuteCommandAsync(
                session.Id,
                "echo 'Performance test'");
            sw.Stop();
            commandExecTimes.Add(sw.Elapsed.TotalMilliseconds);

            Assert.Equal(0, result.ExitCode);
        }

        // Assert - 命令执行时间应在合理范围内
        double avgExecTime = commandExecTimes.Average();
        Assert.True(avgExecTime < 5000, $"Average command execution time ({avgExecTime}ms) should be under 5 seconds");
    }
}

public class Performance_FullWorkflowTest : PerformanceTestBase
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task FullWorkflow_Performance()
    {
        await using TestClientContext context = await CreateContextAsync();
        await using TestSessionTracker sessions = new(context.Client);

        const int TestRounds = 3;
        List<double> totalTimes = new();

        for (int round = 1; round <= TestRounds; round++)
        {
            Stopwatch totalSw = Stopwatch.StartNew();

            // 1. 创建会话
            SessionInfo session = await sessions.CreateSessionAsync(new SessionOptions { Name = $"完整流程-{round}" });

            // 2. 执行一个简单命令
            CommandResult result = await context.Client.ExecuteCommandAsync(
                session.Id,
                "echo 'Hello World!'");
            Assert.Equal(0, result.ExitCode);

            // 3. 上传一个文件
            string filePath = context.GetWorkPath("perf-test.txt");
            await context.Client.UploadFileAsync(
                session.Id,
                filePath,
                "Performance test content"u8.ToArray());

            // 4. 列出目录
            List<FileEntry> files = await context.Client.ListDirectoryAsync(session.Id, context.WorkDir);
            Assert.Contains(files, f => f.Name == "perf-test.txt");

            // 5. 下载文件
            byte[] downloaded = await context.Client.DownloadFileAsync(session.Id, filePath);
            Assert.NotEmpty(downloaded);

            // 6. 删除文件
            await context.Client.DeleteFileAsync(session.Id, filePath);

            // 7. 销毁会话
            await context.Client.DestroySessionAsync(session.Id);

            totalSw.Stop();
            totalTimes.Add(totalSw.Elapsed.TotalMilliseconds);
        }

        // Assert
        double avgTotalTime = totalTimes.Average();
        Assert.True(avgTotalTime < 60000, $"Average full workflow time ({avgTotalTime}ms) should be under 60 seconds");
    }
}
