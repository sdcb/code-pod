namespace CodePod.Sdk.Models;

/// <summary>
/// 容器状态枚举
/// </summary>
public enum ContainerStatus
{
    /// <summary>
    /// 预热中 - 容器正在创建和启动
    /// </summary>
    Warming,

    /// <summary>
    /// 空闲 - 容器已预热完成，等待分配
    /// </summary>
    Idle,

    /// <summary>
    /// 繁忙 - 容器已分配给会话
    /// </summary>
    Busy,

    /// <summary>
    /// 销毁中 - 容器正在被删除
    /// </summary>
    Destroying
}

/// <summary>
/// 容器信息
/// </summary>
public class ContainerInfo
{
    /// <summary>
    /// 容器ID
    /// </summary>
    public required string ContainerId { get; init; }

    /// <summary>
    /// 容器短ID（前12位）
    /// </summary>
    public string ShortId => ContainerId.Length >= 12 ? ContainerId[..12] : ContainerId;

    /// <summary>
    /// 容器名称
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 镜像名称
    /// </summary>
    public required string Image { get; init; }

    /// <summary>
    /// Docker原生状态
    /// </summary>
    public required string DockerStatus { get; init; }

    /// <summary>
    /// 容器状态
    /// </summary>
    public ContainerStatus Status { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// 启动时间
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// 关联的会话ID
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// 容器标签
    /// </summary>
    public Dictionary<string, string> Labels { get; init; } = new();
}
