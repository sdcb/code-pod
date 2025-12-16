namespace DockerShellHost.Models;

/// <summary>
/// 会话状态
/// </summary>
public enum SessionStatus
{
    /// <summary>
    /// 等待分配容器
    /// </summary>
    Queued,

    /// <summary>
    /// 活动中
    /// </summary>
    Active,

    /// <summary>
    /// 已销毁
    /// </summary>
    Destroyed
}

/// <summary>
/// 会话信息
/// </summary>
public class SessionInfo
{
    /// <summary>
    /// 会话ID
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 会话名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 关联的容器ID
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// 会话状态
    /// </summary>
    public SessionStatus Status { get; set; }

    /// <summary>
    /// 队列位置（当状态为Queued时有效）
    /// </summary>
    public int QueuePosition { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// 最后活动时间
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>
    /// 命令执行历史数量
    /// </summary>
    public int CommandCount { get; set; }

    /// <summary>
    /// 是否正在执行命令（用于防止在执行期间被超时清理）
    /// </summary>
    public bool IsExecutingCommand { get; set; }

    /// <summary>
    /// 会话超时时间（秒），null 表示使用系统默认值
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}
