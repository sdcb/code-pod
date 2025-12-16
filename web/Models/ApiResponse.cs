namespace DockerShellHost.Models;

/// <summary>
/// 错误信息
/// </summary>
public class ErrorInfo
{
    /// <summary>
    /// 错误代码
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// 错误详情
    /// </summary>
    public string? Details { get; init; }
}

/// <summary>
/// 统一API响应
/// </summary>
public class ApiResponse<T>
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 数据
    /// </summary>
    public T? Data { get; init; }

    /// <summary>
    /// 错误消息 (简单字符串，向后兼容)
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// 详细错误信息
    /// </summary>
    public ErrorInfo? ErrorInfo { get; set; }

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public static ApiResponse<T> Ok(T data) => new() { Success = true, Data = data };

    public static ApiResponse<T> Fail(string error) => new() { Success = false, Error = error };

    public static ApiResponse<T> Fail(ErrorInfo errorInfo) => new() 
    { 
        Success = false, 
        Error = errorInfo.Message,
        ErrorInfo = errorInfo 
    };
}

/// <summary>
/// 无数据的API响应
/// </summary>
public class ApiResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 消息
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public static ApiResponse Ok(string? message = null) => new() { Success = true, Message = message };

    public static ApiResponse Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// 创建会话请求
/// </summary>
public class CreateSessionRequest
{
    /// <summary>
    /// 会话名称（可选）
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 会话超时时间（秒，可选）
    /// 如果指定，必须小于等于系统配置的 SessionTimeoutSeconds
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// 系统状态
/// </summary>
public class SystemStatus
{
    /// <summary>
    /// 最大容器数配置
    /// </summary>
    public int MaxContainers { get; init; }

    /// <summary>
    /// 可用容器数（空闲状态）
    /// </summary>
    public int AvailableContainers { get; init; }

    /// <summary>
    /// 活动会话数（被赋予session的）
    /// </summary>
    public int ActiveSessions { get; init; }

    /// <summary>
    /// 创建中的容器数
    /// </summary>
    public int WarmingContainers { get; init; }

    /// <summary>
    /// 销毁中的容器数
    /// </summary>
    public int DestroyingContainers { get; init; }

    /// <summary>
    /// Docker镜像
    /// </summary>
    public required string Image { get; init; }

    /// <summary>
    /// 容器列表
    /// </summary>
    public List<ContainerInfo> Containers { get; init; } = [];
}
