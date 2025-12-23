namespace CodePod.Sdk.Models;

/// <summary>
/// 容器资源限制
/// </summary>
public class ResourceLimits
{
    /// <summary>
    /// 内存限制（字节）。默认 512MB
    /// </summary>
    public long MemoryBytes { get; set; } = 512 * 1024 * 1024;

    /// <summary>
    /// CPU 限制（核心数，支持小数）。默认 1.0 核
    /// </summary>
    public double CpuCores { get; set; } = 1.0;

    /// <summary>
    /// 最大进程数。默认 100
    /// </summary>
    public long MaxProcesses { get; set; } = 100;

    /// <summary>
    /// 验证资源限制是否在允许范围内
    /// </summary>
    public void Validate(ResourceLimits maxLimits)
    {
        if (MemoryBytes > maxLimits.MemoryBytes)
            throw new ArgumentException($"Memory limit {MemoryBytes} exceeds maximum {maxLimits.MemoryBytes}");
        if (CpuCores > maxLimits.CpuCores)
            throw new ArgumentException($"CPU limit {CpuCores} exceeds maximum {maxLimits.CpuCores}");
        if (MaxProcesses > maxLimits.MaxProcesses)
            throw new ArgumentException($"Process limit {MaxProcesses} exceeds maximum {maxLimits.MaxProcesses}");
        if (MemoryBytes <= 0)
            throw new ArgumentException("Memory limit must be positive");
        if (CpuCores <= 0)
            throw new ArgumentException("CPU limit must be positive");
        if (MaxProcesses <= 0)
            throw new ArgumentException("Process limit must be positive");
    }

    /// <summary>
    /// 克隆资源限制
    /// </summary>
    public ResourceLimits Clone() => new()
    {
        MemoryBytes = MemoryBytes,
        CpuCores = CpuCores,
        MaxProcesses = MaxProcesses
    };

    /// <summary>
    /// 常用预设：最小配置（适合简单计算）
    /// </summary>
    public static ResourceLimits Minimal => new()
    {
        MemoryBytes = 128 * 1024 * 1024,  // 128MB
        CpuCores = 0.5,
        MaxProcesses = 50
    };

    /// <summary>
    /// 常用预设：标准配置
    /// </summary>
    public static ResourceLimits Standard => new()
    {
        MemoryBytes = 512 * 1024 * 1024,  // 512MB
        CpuCores = 1.0,
        MaxProcesses = 100
    };

    /// <summary>
    /// 常用预设：大型任务（适合数据处理）
    /// </summary>
    public static ResourceLimits Large => new()
    {
        MemoryBytes = 2L * 1024 * 1024 * 1024,  // 2GB
        CpuCores = 2.0,
        MaxProcesses = 200
    };
}

/// <summary>
/// 网络模式
/// </summary>
public enum NetworkMode
{
    /// <summary>
    /// 完全禁用网络（最安全，推荐用于代码执行）
    /// </summary>
    None,

    /// <summary>
    /// 桥接网络（可访问外网）
    /// </summary>
    Bridge,

    /// <summary>
    /// 主机网络（共享主机网络栈，不推荐）
    /// </summary>
    Host
}

/// <summary>
/// 网络模式扩展方法
/// </summary>
public static class NetworkModeExtensions
{
    /// <summary>
    /// 转换为 Docker NetworkMode 字符串
    /// </summary>
    public static string ToDockerNetworkMode(this NetworkMode mode) => mode switch
    {
        NetworkMode.None => "none",
        NetworkMode.Bridge => "bridge",
        NetworkMode.Host => "host",
        _ => "none"
    };
}

/// <summary>
/// 会话使用量统计
/// </summary>
public class SessionUsage
{
    /// <summary>
    /// 会话ID
    /// </summary>
    public int SessionId { get; set; }

    /// <summary>
    /// 容器ID
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// CPU 使用时间（纳秒）
    /// </summary>
    public long CpuUsageNanos { get; set; }

    /// <summary>
    /// 当前内存使用（字节）
    /// </summary>
    public long MemoryUsageBytes { get; set; }

    /// <summary>
    /// 峰值内存使用（字节）
    /// </summary>
    public long PeakMemoryBytes { get; set; }

    /// <summary>
    /// 网络接收字节数
    /// </summary>
    public long NetworkRxBytes { get; set; }

    /// <summary>
    /// 网络发送字节数
    /// </summary>
    public long NetworkTxBytes { get; set; }

    /// <summary>
    /// 已执行命令数
    /// </summary>
    public int CommandCount { get; set; }

    /// <summary>
    /// 统计时间戳
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 会话创建时间
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 会话持续时间
    /// </summary>
    public TimeSpan Duration => DateTimeOffset.UtcNow - CreatedAt;
}

/// <summary>
/// 输出截断策略
/// </summary>
public enum TruncationStrategy
{
    /// <summary>
    /// 保留开头部分
    /// </summary>
    Head,

    /// <summary>
    /// 保留结尾部分
    /// </summary>
    Tail,

    /// <summary>
    /// 保留首尾，中间省略
    /// </summary>
    HeadAndTail
}

/// <summary>
/// 输出配置
/// </summary>
public class OutputOptions
{
    /// <summary>
    /// 最大输出字节数。默认 64KB
    /// </summary>
    public int MaxOutputBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// 截断策略。默认保留首尾
    /// </summary>
    public TruncationStrategy Strategy { get; set; } = TruncationStrategy.HeadAndTail;

    /// <summary>
    /// 截断提示信息
    /// </summary>
    public string TruncationMessage { get; set; } = "\n... [Output truncated: {0} bytes omitted] ...\n";
}
