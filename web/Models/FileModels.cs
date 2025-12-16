namespace DockerShellHost.Models;

/// <summary>
/// 文件/目录条目
/// </summary>
public class FileEntry
{
    /// <summary>
    /// 路径
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// 名称
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 是否为目录
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// 最后修改时间
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }
}

/// <summary>
/// 上传文件请求
/// </summary>
public class UploadFileRequest
{
    /// <summary>
    /// 目标路径（容器内完整路径，如 /app/test.txt）
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// 目标路径（容器内路径）- 兼容旧版
    /// </summary>
    public string? TargetPath { get; init; }

    /// <summary>
    /// 文件名 - 兼容旧版
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// 文件内容（Base64编码）
    /// </summary>
    public required string ContentBase64 { get; init; }

    /// <summary>
    /// 获取完整文件路径
    /// </summary>
    public string GetFullPath()
    {
        if (!string.IsNullOrEmpty(Path))
            return Path;
        if (!string.IsNullOrEmpty(TargetPath) && !string.IsNullOrEmpty(FileName))
            return System.IO.Path.Combine(TargetPath, FileName).Replace('\\', '/');
        throw new ArgumentException("Either 'path' or 'targetPath + fileName' must be provided");
    }
}

/// <summary>
/// 上传文件响应
/// </summary>
public class UploadFileResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 文件完整路径
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// 下载文件响应
/// </summary>
public class DownloadFileResponse
{
    /// <summary>
    /// 文件名
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// 内容类型
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// 文件内容（Base64编码）
    /// </summary>
    public required string ContentBase64 { get; init; }

    /// <summary>
    /// 文件大小
    /// </summary>
    public long Size { get; init; }
}

/// <summary>
/// 目录列表响应
/// </summary>
public class DirectoryListResponse
{
    /// <summary>
    /// 路径
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// 条目列表
    /// </summary>
    public required List<FileEntry> Entries { get; init; }

    /// <summary>
    /// 总条目数
    /// </summary>
    public int TotalCount { get; init; }
}
