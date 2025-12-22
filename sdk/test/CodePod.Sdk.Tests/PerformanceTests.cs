using System.Diagnostics;
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
        var sessionCreateTimes = new List<double>();
        var createdSessions = new List<string>();

        try
        {
            for (int round = 1; round <= TestRounds; round++)
            {
                var sw = Stopwatch.StartNew();

                // 创建会话
                var session = await Client.CreateSessionAsync($"性能测试-{round}");
                createdSessions.Add(session.SessionId);

                // 等待会话就绪
                await WaitForSessionReadyAsync(session.SessionId, 60);

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
        finally
        {
            foreach (var sessionId in createdSessions)
            {
                try
                {
                    await Client.DestroySessionAsync(sessionId);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    [Fact]
    public async Task CommandExecution_Performance()
    {
        // Arrange
        var session = await Client.CreateSessionAsync("命令性能测试");
        await WaitForSessionReadyAsync(session.SessionId);

        var commandExecTimes = new List<double>();

        try
        {
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                
                var result = await Client.ExecuteCommandAsync(
                    session.SessionId,
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
        finally
        {
            await Client.DestroySessionAsync(session.SessionId);
        }
    }

    [Fact]
    public async Task FullWorkflow_Performance()
    {
        var totalTimes = new List<double>();

        try
        {
            for (int round = 1; round <= TestRounds; round++)
            {
                var totalSw = Stopwatch.StartNew();

                // 1. 创建会话
                var session = await Client.CreateSessionAsync($"完整流程-{round}");
                await WaitForSessionReadyAsync(session.SessionId);

                // 2. 执行一个简单命令
                var result = await Client.ExecuteCommandAsync(
                    session.SessionId,
                    "echo 'Hello World!'");
                Assert.Equal(0, result.ExitCode);

                // 3. 上传一个文件
                await Client.UploadFileAsync(
                    session.SessionId,
                    "/app/perf-test.txt",
                    "Performance test content"u8.ToArray());

                // 4. 列出目录
                var files = await Client.ListDirectoryAsync(session.SessionId, "/app");
                Assert.Contains(files, f => f.Name == "perf-test.txt");

                // 5. 下载文件
                var downloaded = await Client.DownloadFileAsync(session.SessionId, "/app/perf-test.txt");
                Assert.NotEmpty(downloaded);

                // 6. 删除文件
                await Client.DeleteFileAsync(session.SessionId, "/app/perf-test.txt");

                // 7. 销毁会话
                await Client.DestroySessionAsync(session.SessionId);

                totalSw.Stop();
                totalTimes.Add(totalSw.Elapsed.TotalMilliseconds);
            }

            // 统计
            var avgTotalTime = totalTimes.Average();
            
            // Assert
            Assert.True(avgTotalTime < 60000, $"Average full workflow time ({avgTotalTime}ms) should be under 60 seconds");
        }
        catch
        {
            // Cleanup any remaining sessions
            var sessions = await Client.GetAllSessionsAsync();
            foreach (var session in sessions)
            {
                try
                {
                    await Client.DestroySessionAsync(session.SessionId);
                }
                catch
                {
                    // Ignore
                }
            }
            throw;
        }
    }
}
