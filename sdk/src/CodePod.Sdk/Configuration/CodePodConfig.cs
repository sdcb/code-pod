using CodePod.Sdk.Models;

namespace CodePod.Sdk.Configuration;

/// <summary>
/// CodePod SDK 配置
/// </summary>
public class CodePodConfig
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
    public string LabelPrefix { get; set; } = "codepod";

    /// <summary>
    /// 默认资源限制（用于创建容器时的最大上限）
    /// </summary>
    public ResourceLimits MaxResourceLimits { get; set; } = ResourceLimits.Standard;

    /// <summary>
    /// 默认资源限制（用于不指定限制时的默认值）
    /// </summary>
    public ResourceLimits DefaultResourceLimits { get; set; } = ResourceLimits.Standard;

    /// <summary>
    /// 默认网络模式。推荐使用 None 以获得最佳安全性
    /// </summary>
    public NetworkMode DefaultNetworkMode { get; set; } = NetworkMode.None;

    /// <summary>
    /// 输出配置
    /// </summary>
    public OutputOptions OutputOptions { get; set; } = new();

    /// <summary>
    /// Artifacts 目录路径（相对于 WorkDir）
    /// </summary>
    public string ArtifactsDir { get; set; } = "artifacts";
}

