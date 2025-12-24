namespace CodePod.Sdk.Models;

/// <summary>
/// 系统状态信息
/// </summary>
public class SystemStatus
{
    /// <summary>
    /// 可用容器数
    /// </summary>
    public int AvailableContainers { get; init; }

    /// <summary>
    /// 繁忙容器数
    /// </summary>
    public int BusyContainers { get; init; }

    /// <summary>
    /// 预热中容器数
    /// </summary>
    public int WarmingContainers { get; init; }

    /// <summary>
    /// 销毁中容器数
    /// </summary>
    public int DestroyingContainers { get; init; }

    /// <summary>
    /// 最大容器数
    /// </summary>
    public int MaxContainers { get; init; }

    /// <summary>
    /// 活动会话数
    /// </summary>
    public int ActiveSessions { get; init; }

    /// <summary>
    /// 等待中会话数
    /// </summary>
    public int QueuedSessions { get; init; }
}
