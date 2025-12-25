using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public abstract class CodePodFixtureBase : IAsyncLifetime
{
    public CodePodClient Client { get; private set; } = null!;
    public CodePodConfig Config { get; private set; } = null!;
    public ILoggerFactory LoggerFactory { get; private set; } = null!;

    public bool IsWindowsContainer => Config.IsWindowsContainer;
    public string WorkDir => Config.WorkDir;

    protected abstract CodePodConfig CreateConfig(CodePodTestSettings settings);

    public async Task InitializeAsync()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        CodePodTestSettings settings = TestSettings.Load();
        Config = CreateConfig(settings);

        Client = new CodePodClientBuilder()
            .WithConfig(Config)
            .WithLogging(LoggerFactory)
            .Build();

        await Client.InitializeAsync();
    }

    public virtual async Task DisposeAsync()
    {
        try
        {
            IReadOnlyList<SessionInfo> sessions = await Client.GetAllSessionsAsync();
            foreach (SessionInfo session in sessions)
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
            try
            {
                Client.Dispose();
            }
            catch
            {
                // Ignore
            }

            try
            {
                LoggerFactory.Dispose();
            }
            catch
            {
                // Ignore
            }
        }
    }

    public string GetWorkPath(string relativePath) =>
        CodePodTestSupport.GetWorkPath(Config, relativePath);

    public string GetEchoCommand(string message) =>
        CodePodTestSupport.GetEchoCommand(IsWindowsContainer, message);

    public string GetMultiLineEchoCommand(int lineCount) =>
        CodePodTestSupport.GetMultiLineEchoCommand(IsWindowsContainer, lineCount);

    public string GetStreamingOutputCommand(int lineCount, double delaySeconds = 0.1) =>
        CodePodTestSupport.GetStreamingOutputCommand(IsWindowsContainer, lineCount, delaySeconds);
}
