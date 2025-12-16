namespace DockerShellHost.Exceptions;

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
