namespace CodePod.Sdk.Models;

/// <summary>
/// 命令执行请求
/// </summary>
public class CommandRequest
{
    /// <summary>
    /// 要执行的Shell命令
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// 工作目录（相对于容器根目录）
    /// </summary>
    public string WorkingDirectory { get; init; } = "/app";

    /// <summary>
    /// 超时时间（秒）
    /// </summary>
    public int TimeoutSeconds { get; init; } = 60;
}

/// <summary>
/// 命令执行结果（完整结果，用于非流式响应）
/// </summary>
public class CommandResult
{
    /// <summary>
    /// 标准输出
    /// </summary>
    public string Stdout { get; init; } = string.Empty;

    /// <summary>
    /// 标准错误
    /// </summary>
    public string Stderr { get; init; } = string.Empty;

    /// <summary>
    /// 退出码
    /// </summary>
    public long ExitCode { get; init; }

    /// <summary>
    /// 执行时间（毫秒）
    /// </summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 命令输出流事件类型
/// </summary>
public enum CommandOutputType
{
    /// <summary>
    /// 标准输出
    /// </summary>
    Stdout,

    /// <summary>
    /// 标准错误
    /// </summary>
    Stderr,

    /// <summary>
    /// 命令完成
    /// </summary>
    Exit
}

/// <summary>
/// 命令流式输出事件
/// </summary>
public class CommandOutputEvent
{
    /// <summary>
    /// 事件类型
    /// </summary>
    public CommandOutputType Type { get; init; }

    /// <summary>
    /// 输出数据（当 Type 为 Stdout 或 Stderr 时）
    /// </summary>
    public string? Data { get; init; }

    /// <summary>
    /// 退出码（当 Type 为 Exit 时）
    /// </summary>
    public long? ExitCode { get; init; }

    /// <summary>
    /// 执行时间（当 Type 为 Exit 时）
    /// </summary>
    public long? ExecutionTimeMs { get; init; }

    public static CommandOutputEvent FromStdout(string data) =>
        new() { Type = CommandOutputType.Stdout, Data = data };

    public static CommandOutputEvent FromStderr(string data) =>
        new() { Type = CommandOutputType.Stderr, Data = data };

    public static CommandOutputEvent FromExit(long exitCode, long executionTimeMs) =>
        new() { Type = CommandOutputType.Exit, ExitCode = exitCode, ExecutionTimeMs = executionTimeMs };
}
