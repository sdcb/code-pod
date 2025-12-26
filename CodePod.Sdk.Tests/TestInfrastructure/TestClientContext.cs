using CodePod.Sdk.Configuration;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class TestClientContext : IAsyncDisposable
{
    public CodePodClient Client { get; }
    public CodePodConfig Config { get; }
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// 是否使用 Mock Docker（用于区分单元测试和集成测试）
    /// </summary>
    public bool UseMockDocker { get; }

    public bool IsWindowsContainer => Config.IsWindowsContainer;
    public string WorkDir => Config.WorkDir;

    private TestClientContext(CodePodClient client, CodePodConfig config, ILoggerFactory loggerFactory, bool useMockDocker)
    {
        Client = client;
        Config = config;
        LoggerFactory = loggerFactory;
        UseMockDocker = useMockDocker;
    }

    public static async Task<TestClientContext> CreateAsync(
        Action<CodePodConfig>? configure = null,
        LogLevel logLevel = LogLevel.Warning,
        CancellationToken cancellationToken = default)
    {
        ILoggerFactory loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(logLevel);
        });

        CodePodTestSettings settings = TestSettings.Load();
        CodePodConfig config = CodePodTestSupport.CreateDefaultConfig(settings);

        // Defaults for tests: no prewarm, isolate by label prefix.
        config.PrewarmCount = 0;
        config.LabelPrefix = $"codepod-test-{Guid.NewGuid():N}";

        configure?.Invoke(config);

        CodePodClientBuilder builder = new CodePodClientBuilder()
            .WithConfig(config)
            .WithLogging(loggerFactory)
            .UseInMemoryDatabase();

        // 根据配置决定是否使用 Mock Docker
        if (settings.UseMockDocker)
        {
            builder.WithDockerService(new MockDockerService(config));
        }

        CodePodClient client = await builder.BuildAsync(syncInitialState: false, preloadImages: false, cancellationToken);

        return new TestClientContext(client, config, loggerFactory, settings.UseMockDocker);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DeleteAllContainersAsync(CancellationToken.None);
        Client.Dispose();
        LoggerFactory.Dispose();
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
