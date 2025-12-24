using CodePod.Sdk.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class CodePodFixture : IAsyncLifetime
{
    public CodePodClient Client { get; private set; } = null!;
    public CodePodConfig Config { get; private set; } = null!;
    public ILoggerFactory LoggerFactory { get; private set; } = null!;

    public bool IsWindowsContainer => Config.IsWindowsContainer;
    public string WorkDir => Config.WorkDir;

    public async Task InitializeAsync()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var settings = TestSettings.Load();
        Config = CodePodTestSupport.CreateDefaultConfig(settings);

        Client = new CodePodClientBuilder()
            .WithConfig(Config)
            .WithLogging(LoggerFactory)
            .Build();

        await Client.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            Client.Dispose();
            await Task.Delay(100);

            var sessions = await Client.GetAllSessionsAsync();
            foreach (var session in sessions)
            {
                try
                {
                    await Client.DestroySessionAsync(session.Id);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            await Client.DeleteAllContainersAsync(CancellationToken.None);
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            LoggerFactory.Dispose();
        }
    }

    public Task<Models.SessionInfo> WaitForSessionReadyAsync(int sessionId, int maxWaitSeconds = 30) =>
        CodePodTestSupport.WaitForSessionReadyAsync(Client, sessionId, maxWaitSeconds);

    public string GetWorkPath(string relativePath) =>
        CodePodTestSupport.GetWorkPath(Config, relativePath);

    public string GetEchoCommand(string message) =>
        CodePodTestSupport.GetEchoCommand(IsWindowsContainer, message);

    public string GetMultiLineEchoCommand(int lineCount) =>
        CodePodTestSupport.GetMultiLineEchoCommand(IsWindowsContainer, lineCount);

    public string GetStreamingOutputCommand(int lineCount, double delaySeconds = 0.1) =>
        CodePodTestSupport.GetStreamingOutputCommand(IsWindowsContainer, lineCount, delaySeconds);
}
