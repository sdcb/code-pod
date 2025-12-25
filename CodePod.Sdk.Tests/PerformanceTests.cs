using CodePod.Sdk.Models;
using System.Diagnostics;
using CodePod.Sdk.Tests.TestInfrastructure;
using Xunit;

namespace CodePod.Sdk.Tests;

/// <summary>
/// 性能测试 - 对应 test/PerformanceTest.cs
/// </summary>
public class PerformanceTests : TestBase
{
    private const int TestRounds = 3;

    [Fact]
    public async Task SessionCreation_Performance()
    {
        await using TestSessionTracker sessions = new(Client);
        List<double> sessionCreateTimes = new();

        for (int round = 1; round <= TestRounds; round++)
        {
            Stopwatch sw = Stopwatch.StartNew();

            // 创建会话
            SessionInfo session = await sessions.CreateSessionAsync($"性能测试-{round}");

            sw.Stop();
            sessionCreateTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        // 计算统计
        var avgCreateTime = sessionCreateTimes.Average();
        var minCreateTime = sessionCreateTimes.Min();
        var maxCreateTime = sessionCreateTimes.Max();

        // Assert - 会话创建时间应在合理范围内
        Assert.True(avgCreateTime < 30000, $"Average session creation time ({avgCreateTime}ms) should be under 30 seconds");
    }

    [Fact]
    public async Task CommandExecution_Performance()
    {
        await using TestSessionTracker sessions = new(Client);
        // Arrange
        SessionInfo session = await sessions.CreateSessionAsync("命令性能测试");

        List<double> commandExecTimes = new();


        for (int i = 0; i < 5; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();

            CommandResult result = await Client.ExecuteCommandAsync(
                session.Id,
                "echo 'Performance test'");
            
            sw.Stop();
            commandExecTimes.Add(sw.Elapsed.TotalMilliseconds);

            Assert.Equal(0, result.ExitCode);
        }

        // 统计
        var avgExecTime = commandExecTimes.Average();
        
        // Assert - 命令执行时间应在合理范围内
        Assert.True(avgExecTime < 5000, $"Average command execution time ({avgExecTime}ms) should be under 5 seconds");
    }

    [Fact]
    public async Task FullWorkflow_Performance()
    {
        await using TestSessionTracker sessions = new(Client);
        List<double> totalTimes = new();

        for (int round = 1; round <= TestRounds; round++)
        {
            Stopwatch totalSw = Stopwatch.StartNew();

            // 1. 创建会话
            SessionInfo session = await sessions.CreateSessionAsync($"完整流程-{round}");

            // 2. 执行一个简单命令
            CommandResult result = await Client.ExecuteCommandAsync(
                session.Id,
                "echo 'Hello World!'");
            Assert.Equal(0, result.ExitCode);

            // 3. 上传一个文件
            var filePath = GetWorkPath("perf-test.txt");
            await Client.UploadFileAsync(
                session.Id,
                filePath,
                "Performance test content"u8.ToArray());

            // 4. 列出目录
            List<FileEntry> files = await Client.ListDirectoryAsync(session.Id, WorkDir);
            Assert.Contains(files, f => f.Name == "perf-test.txt");

            // 5. 下载文件
            var downloaded = await Client.DownloadFileAsync(session.Id, filePath);
            Assert.NotEmpty(downloaded);

            // 6. 删除文件
            await Client.DeleteFileAsync(session.Id, filePath);

            // 7. 销毁会话
            await Client.DestroySessionAsync(session.Id);

            totalSw.Stop();
            totalTimes.Add(totalSw.Elapsed.TotalMilliseconds);
        }

        // 统计
        var avgTotalTime = totalTimes.Average();
        
        // Assert
        Assert.True(avgTotalTime < 60000, $"Average full workflow time ({avgTotalTime}ms) should be under 60 seconds");
    }
}
