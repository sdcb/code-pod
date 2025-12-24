using CodePod.Sdk.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodePod.Sdk.Storage;

/// <summary>
/// CodePod 数据库上下文
/// </summary>
public class CodePodDbContext : DbContext
{
    public DbSet<ContainerEntity> Containers { get; set; } = null!;
    public DbSet<SessionEntity> Sessions { get; set; } = null!;

    public CodePodDbContext(DbContextOptions<CodePodDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Container 实体配置
        modelBuilder.Entity<ContainerEntity>(entity =>
        {
            entity.HasKey(e => e.ContainerId);
            entity.Property(e => e.ContainerId).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Image).HasMaxLength(256).IsRequired();
            entity.Property(e => e.DockerStatus).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.LabelsJson).HasColumnName("Labels");

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SessionId);
        });

        // Session 实体配置
        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.ContainerId).HasMaxLength(64);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.NetworkMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.ResourceLimitsJson).HasColumnName("ResourceLimits");

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ContainerId);
            entity.HasIndex(e => e.QueuePosition);
        });
    }
}

/// <summary>
/// 容器数据库实体
/// </summary>
public class ContainerEntity
{
    public required string ContainerId { get; set; }
    public required string Name { get; set; }
    public required string Image { get; set; }
    public required string DockerStatus { get; set; }
    public ContainerStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public int? SessionId { get; set; }

    /// <summary>
    /// Labels 的 JSON 序列化存储
    /// </summary>
    public string? LabelsJson { get; set; }

    /// <summary>
    /// 从实体转换为模型
    /// </summary>
    public ContainerInfo ToModel()
    {
        var labels = string.IsNullOrEmpty(LabelsJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(LabelsJson) ?? new Dictionary<string, string>();

        return new ContainerInfo
        {
            ContainerId = ContainerId,
            Name = Name,
            Image = Image,
            DockerStatus = DockerStatus,
            Status = Status,
            CreatedAt = CreatedAt,
            StartedAt = StartedAt,
            SessionId = SessionId,
            Labels = labels
        };
    }

    /// <summary>
    /// 从模型创建实体
    /// </summary>
    public static ContainerEntity FromModel(ContainerInfo model)
    {
        return new ContainerEntity
        {
            ContainerId = model.ContainerId,
            Name = model.Name,
            Image = model.Image,
            DockerStatus = model.DockerStatus,
            Status = model.Status,
            CreatedAt = model.CreatedAt,
            StartedAt = model.StartedAt,
            SessionId = model.SessionId,
            LabelsJson = model.Labels.Count > 0 ? JsonSerializer.Serialize(model.Labels) : null
        };
    }

    /// <summary>
    /// 从模型更新实体
    /// </summary>
    public void UpdateFromModel(ContainerInfo model)
    {
        Status = model.Status;
        SessionId = model.SessionId;
        LabelsJson = model.Labels.Count > 0 ? JsonSerializer.Serialize(model.Labels) : null;
    }
}

/// <summary>
/// 会话数据库实体
/// </summary>
public class SessionEntity
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? ContainerId { get; set; }
    public SessionStatus Status { get; set; }
    public int QueuePosition { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int CommandCount { get; set; }
    public bool IsExecutingCommand { get; set; }
    public int? TimeoutSeconds { get; set; }
    public NetworkMode NetworkMode { get; set; }

    /// <summary>
    /// ResourceLimits 的 JSON 序列化存储
    /// </summary>
    public string? ResourceLimitsJson { get; set; }

    /// <summary>
    /// 从实体转换为模型
    /// </summary>
    public SessionInfo ToModel()
    {
        var resourceLimits = string.IsNullOrEmpty(ResourceLimitsJson)
            ? null
            : JsonSerializer.Deserialize<ResourceLimits>(ResourceLimitsJson);

        return new SessionInfo
        {
            Id = Id,
            Name = Name,
            ContainerId = ContainerId,
            Status = Status,
            QueuePosition = QueuePosition,
            CreatedAt = CreatedAt,
            LastActivityAt = LastActivityAt,
            CommandCount = CommandCount,
            IsExecutingCommand = IsExecutingCommand,
            TimeoutSeconds = TimeoutSeconds,
            ResourceLimits = resourceLimits,
            NetworkMode = NetworkMode
        };
    }

    /// <summary>
    /// 从模型创建实体
    /// </summary>
    public static SessionEntity FromModel(SessionInfo model)
    {
        return new SessionEntity
        {
            Id = model.Id,
            Name = model.Name,
            ContainerId = model.ContainerId,
            Status = model.Status,
            QueuePosition = model.QueuePosition,
            CreatedAt = model.CreatedAt,
            LastActivityAt = model.LastActivityAt,
            CommandCount = model.CommandCount,
            IsExecutingCommand = model.IsExecutingCommand,
            TimeoutSeconds = model.TimeoutSeconds,
            ResourceLimitsJson = model.ResourceLimits != null ? JsonSerializer.Serialize(model.ResourceLimits) : null,
            NetworkMode = model.NetworkMode
        };
    }

    /// <summary>
    /// 从模型更新实体
    /// </summary>
    public void UpdateFromModel(SessionInfo model)
    {
        Name = model.Name;
        ContainerId = model.ContainerId;
        Status = model.Status;
        QueuePosition = model.QueuePosition;
        LastActivityAt = model.LastActivityAt;
        CommandCount = model.CommandCount;
        IsExecutingCommand = model.IsExecutingCommand;
        TimeoutSeconds = model.TimeoutSeconds;
        ResourceLimitsJson = model.ResourceLimits != null ? JsonSerializer.Serialize(model.ResourceLimits) : null;
        NetworkMode = model.NetworkMode;
    }
}
