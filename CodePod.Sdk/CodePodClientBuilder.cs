using CodePod.Sdk.Configuration;
using CodePod.Sdk.Services;
using CodePod.Sdk.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk;

/// <summary>
/// CodePodClient 构建器
/// </summary>
public class CodePodClientBuilder
{
    private CodePodConfig _config = new();
    private ILoggerFactory? _loggerFactory;
    private IDbContextFactory<CodePodDbContext>? _dbContextFactory;
    private Action<DbContextOptionsBuilder>? _dbContextOptionsAction;
    private bool _useInMemoryDatabase = false;
    private string? _inMemoryDatabaseName;

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
    /// 使用日志工厂
    /// </summary>
    public CodePodClientBuilder WithLogging(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// 使用 EF Core InMemory 数据库（适合测试）
    /// </summary>
    /// <param name="databaseName">数据库名称，默认为随机生成</param>
    public CodePodClientBuilder UseInMemoryDatabase(string? databaseName = null)
    {
        _useInMemoryDatabase = true;
        _inMemoryDatabaseName = databaseName ?? $"CodePod_{Guid.NewGuid():N}";
        return this;
    }

    /// <summary>
    /// 使用自定义数据库配置（例如 SQLite、SQL Server 等）
    /// </summary>
    /// <param name="optionsAction">DbContext 配置委托</param>
    /// <example>
    /// // 使用 SQLite
    /// builder.UseDatabase(options => options.UseSqlite("Data Source=codepod.db"), enableStateSync: true);
    /// 
    /// // 使用 SQL Server
    /// builder.UseDatabase(options => options.UseSqlServer(connectionString), enableStateSync: true);
    /// </example>
    public CodePodClientBuilder UseDatabase(Action<DbContextOptionsBuilder> optionsAction)
    {
        _dbContextOptionsAction = optionsAction;
        return this;
    }

    /// <summary>
    /// 使用自定义 DbContext 工厂（高级场景）
    /// </summary>
    /// <param name="factory">DbContext 工厂实例</param>
    public CodePodClientBuilder WithDbContextFactory(IDbContextFactory<CodePodDbContext> factory)
    {
        _dbContextFactory = factory;
        return this;
    }

    /// <summary>
    /// 构建并初始化 CodePodClient 实例
    /// </summary>
    public async Task<CodePodClient> BuildAsync(
        bool syncInitialState = false,
        bool preloadImages = false,
        CancellationToken cancellationToken = default)
    {
        // 创建 DbContext 工厂
        IDbContextFactory<CodePodDbContext> dbContextFactory = CreateDbContextFactory();

        // 确保数据库已创建
        await using (CodePodDbContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
        }

        DockerService dockerService = new(
            _config,
            _loggerFactory?.CreateLogger<DockerService>());

        DockerPoolService poolService = new(
            dockerService,
            dbContextFactory,
            _config,
            _loggerFactory?.CreateLogger<DockerPoolService>());

        SessionService sessionService = new(
            dbContextFactory,
            poolService,
            _config,
            _loggerFactory?.CreateLogger<SessionService>());

        SessionCleanupService cleanupService = new(
            dbContextFactory,
            sessionService,
            _config,
            _loggerFactory?.CreateLogger<SessionCleanupService>());

        CodePodClient client = new(
            dockerService,
            poolService,
            sessionService,
            cleanupService,
            dbContextFactory,
            _config);

        await client.InitializeAsync(syncInitialState: syncInitialState, preloadImages: preloadImages, cancellationToken);
        return client;
    }

    private IDbContextFactory<CodePodDbContext> CreateDbContextFactory()
    {
        // 优先使用显式提供的工厂
        if (_dbContextFactory != null)
        {
            return _dbContextFactory;
        }

        DbContextOptionsBuilder<CodePodDbContext> optionsBuilder = new();

        if (_useInMemoryDatabase)
        {
            // 使用 InMemory 数据库
            optionsBuilder.UseInMemoryDatabase(_inMemoryDatabaseName!);
        }
        else if (_dbContextOptionsAction != null)
        {
            // 使用自定义配置
            _dbContextOptionsAction(optionsBuilder);
        }
        else
        {
            // 默认使用 InMemory 数据库
            _useInMemoryDatabase = true;
            _inMemoryDatabaseName = $"CodePod_{Guid.NewGuid():N}";
            optionsBuilder.UseInMemoryDatabase(_inMemoryDatabaseName);
        }

        return new CodePodDbContextFactory(optionsBuilder.Options);
    }
}

/// <summary>
/// 简单的 DbContext 工厂实现
/// </summary>
internal class CodePodDbContextFactory : IDbContextFactory<CodePodDbContext>
{
    private readonly DbContextOptions<CodePodDbContext> _options;

    public CodePodDbContextFactory(DbContextOptions<CodePodDbContext> options)
    {
        _options = options;
    }

    public CodePodDbContext CreateDbContext()
    {
        return new CodePodDbContext(_options);
    }
}
