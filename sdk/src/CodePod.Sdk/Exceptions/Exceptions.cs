namespace CodePod.Sdk.Exceptions;

/// <summary>
/// Docker 连接异常
/// </summary>
public class DockerConnectionException : Exception
{
    public DockerConnectionException(string message) : base(message) { }
    public DockerConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// 容器不存在异常
/// </summary>
public class ContainerNotFoundException : Exception
{
    public string ContainerId { get; }

    public ContainerNotFoundException(string containerId)
        : base($"Container {containerId} not found or has been deleted")
    {
        ContainerId = containerId;
    }

    public ContainerNotFoundException(string containerId, Exception innerException)
        : base($"Container {containerId} not found or has been deleted", innerException)
    {
        ContainerId = containerId;
    }
}

/// <summary>
/// Docker 操作异常
/// </summary>
public class DockerOperationException : Exception
{
    public string Operation { get; }

    public DockerOperationException(string operation, string message)
        : base($"Docker operation '{operation}' failed: {message}")
    {
        Operation = operation;
    }

    public DockerOperationException(string operation, string message, Exception innerException)
        : base($"Docker operation '{operation}' failed: {message}", innerException)
    {
        Operation = operation;
    }
}

/// <summary>
/// 会话不存在异常
/// </summary>
public class SessionNotFoundException : Exception
{
    public int SessionId { get; }

    public SessionNotFoundException(int sessionId)
        : base($"Session {sessionId} not found")
    {
        SessionId = sessionId;
    }
}

/// <summary>
/// 会话未就绪异常（没有分配容器）
/// </summary>
public class SessionNotReadyException : Exception
{
    public int SessionId { get; }

    public SessionNotReadyException(int sessionId)
        : base($"Session {sessionId} is not ready (no container assigned)")
    {
        SessionId = sessionId;
    }
}

/// <summary>
/// 容器配额已满异常
/// </summary>
public class MaxContainersReachedException : Exception
{
    public int MaxContainers { get; }

    public MaxContainersReachedException(int maxContainers)
        : base($"Maximum container count ({maxContainers}) reached")
    {
        MaxContainers = maxContainers;
    }
}

/// <summary>
/// 超时时间超出限制异常
/// </summary>
public class TimeoutExceedsLimitException : Exception
{
    public int RequestedTimeout { get; }
    public int MaxTimeout { get; }

    public TimeoutExceedsLimitException(int requestedTimeout, int maxTimeout)
        : base($"Requested timeout ({requestedTimeout}s) cannot exceed system limit ({maxTimeout}s)")
    {
        RequestedTimeout = requestedTimeout;
        MaxTimeout = maxTimeout;
    }
}
