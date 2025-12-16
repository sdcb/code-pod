namespace DockerShellHost.Configuration;

/// <summary>
/// Docker池配置
/// </summary>
public class DockerPoolConfig
{
    /// <summary>
    /// Docker镜像名称
    /// </summary>
    public string Image { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>
    /// 预热容器数量
    /// </summary>
    public int PrewarmCount { get; set; } = 2;

    /// <summary>
    /// 最大容器数量
    /// </summary>
    public int MaxContainers { get; set; } = 10;

    /// <summary>
    /// 会话超时时间（秒）
    /// </summary>
    public int SessionTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// 容器工作目录
    /// </summary>
    public string WorkDir { get; set; } = "/app";

    /// <summary>
    /// 容器标签前缀，用于标识由本系统管理的容器
    /// </summary>
    public string LabelPrefix { get; set; } = "dockershellhost";
}
