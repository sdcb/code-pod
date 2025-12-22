using CodePod.Sdk.Configuration;
using CodePod.Sdk.Services;
using CodePod.Sdk.Storage;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk;

/// <summary>
/// CodePodClient 构建器
/// </summary>
public class CodePodClientBuilder
{
    private CodePodConfig _config = new();
    private ISessionStorage? _sessionStorage;
    private IContainerStorage? _containerStorage;
    private IDockerClientFactory? _dockerClientFactory;
    private ILoggerFactory? _loggerFactory;

    /// <summary>
    /// 配置选项
    /// </summary>
    public CodePodClientBuilder Configure(Action<CodePodConfig> configure)
    {
        configure(_config);
        return this;
    }

    /// <summary>
    /// 使用自定义配置
    /// </summary>
    public CodePodClientBuilder WithConfig(CodePodConfig config)
    {
        _config = config;
        return this;
    }

    /// <summary>
    /// 使用自定义会话存储
    /// </summary>
    public CodePodClientBuilder WithSessionStorage(ISessionStorage storage)
    {
        _sessionStorage = storage;
        return this;
    }

    /// <summary>
    /// 使用自定义容器存储
    /// </summary>
    public CodePodClientBuilder WithContainerStorage(IContainerStorage storage)
    {
        _containerStorage = storage;
        return this;
    }

    /// <summary>
    /// 使用自定义 Docker 客户端工厂
    /// </summary>
    public CodePodClientBuilder WithDockerClientFactory(IDockerClientFactory factory)
    {
        _dockerClientFactory = factory;
        return this;
    }

    /// <summary>
    /// 使用日志工厂
    /// </summary>
    public CodePodClientBuilder WithLogging(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// 构建 CodePodClient 实例
    /// </summary>
    public CodePodClient Build()
    {
        var sessionStorage = _sessionStorage ?? new InMemorySessionStorage();
        var containerStorage = _containerStorage ?? new InMemoryContainerStorage();
        var dockerClientFactory = _dockerClientFactory ?? new DockerClientFactory();

        var dockerService = new DockerService(
            dockerClientFactory, 
            _config, 
            _loggerFactory?.CreateLogger<DockerService>());

        var poolService = new DockerPoolService(
            dockerService,
            containerStorage,
            _config,
            _loggerFactory?.CreateLogger<DockerPoolService>());

        var sessionService = new SessionService(
            sessionStorage,
            poolService,
            _config,
            _loggerFactory?.CreateLogger<SessionService>());

        var cleanupService = new SessionCleanupService(
            sessionStorage,
            sessionService,
            _config,
            _loggerFactory?.CreateLogger<SessionCleanupService>());

        return new CodePodClient(
            dockerService,
            poolService,
            sessionService,
            cleanupService,
            containerStorage,
            _config);
    }
}
