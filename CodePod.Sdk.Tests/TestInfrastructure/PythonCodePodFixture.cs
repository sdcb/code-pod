using CodePod.Sdk.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class PythonCodePodFixture : IAsyncLifetime
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

        CodePodTestSettings settings = TestSettings.Load();
        CodePodConfig config = CodePodTestSupport.CreateDefaultConfig(settings);

        config.Image = settings.IsWindowsContainer ? settings.PythonWindowsImage : settings.PythonLinuxImage;
        config.PrewarmCount = 1;
        config.SessionTimeoutSeconds = 300;
        config.LabelPrefix = "codepod-cmdarray-test";

        Config = config;

        Client = new CodePodClientBuilder()
            .WithConfig(Config)
            .WithLogging(LoggerFactory)
            .Build();

        await Client.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await Client.DeleteAllContainersAsync();
        Client.Dispose();
        LoggerFactory.Dispose();
    }

    public Task<Models.SessionInfo> WaitForSessionReadyAsync(int sessionId, int maxWaitSeconds = 30) =>
        CodePodTestSupport.WaitForSessionReadyAsync(Client, sessionId, maxWaitSeconds);

    public string GetWorkPath(string relativePath) =>
        CodePodTestSupport.GetWorkPath(Config, relativePath);
}
