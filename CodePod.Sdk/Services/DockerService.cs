using System.Runtime.CompilerServices;
using System.Text;
using CodePod.Sdk.Configuration;
using CodePod.Sdk.Exceptions;
using CodePod.Sdk.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SdkContainerStatus = CodePod.Sdk.Models.ContainerStatus;

namespace CodePod.Sdk.Services;

/// <summary>
/// Docker服务接口
/// </summary>
public interface IDockerService : IDisposable
{
    /// <summary>
    /// 确保镜像存在
    /// </summary>
    Task EnsureImageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建并启动容器（使用默认资源限制和网络模式）
    /// </summary>
    Task<ContainerInfo> CreateContainerAsync(int? sessionId = null, bool isWarm = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建并启动容器（指定资源限制和网络模式）
    /// </summary>
    Task<ContainerInfo> CreateContainerAsync(int? sessionId, bool isWarm, ResourceLimits? resourceLimits, NetworkMode? networkMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有受管理的容器
    /// </summary>
    Task<List<ContainerInfo>> GetManagedContainersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取容器详情
    /// </summary>
    Task<ContainerInfo?> GetContainerAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除容器
    /// </summary>
    Task DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除所有受管理的容器
    /// </summary>
    Task DeleteAllManagedContainersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行 shell 命令
    /// </summary>
    Task<CommandResult> ExecuteCommandAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行命令数组（直接执行，不经过 shell 包装）
    /// </summary>
    Task<CommandResult> ExecuteCommandAsync(string containerId, string[] command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式执行 shell 命令
    /// </summary>
    IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式执行命令数组（直接执行，不经过 shell 包装）
    /// </summary>
    IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(string containerId, string[] command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 上传文件到容器
    /// </summary>
    Task UploadFileAsync(string containerId, string containerPath, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出容器中的目录
    /// </summary>
    Task<List<FileEntry>> ListDirectoryAsync(string containerId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从容器下载文件
    /// </summary>
    Task<byte[]> DownloadFileAsync(string containerId, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新容器的会话标签
    /// </summary>
    Task AssignSessionToContainerAsync(string containerId, int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取容器使用量统计
    /// </summary>
    Task<SessionUsage?> GetContainerStatsAsync(string containerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Docker服务实现
/// </summary>
public class DockerService : IDockerService
{
    private readonly DockerClient _client;
    private readonly CodePodConfig _config;
    private readonly ILogger<DockerService>? _logger;


    public DockerService(CodePodConfig config, ILogger<DockerService>? logger = null)
    {
        _client = new DockerClientConfiguration(config.GetDockerEndpointUri()).CreateClient();
        _config = config;
        _logger = logger;
    }

    public async Task EnsureImageAsync(CancellationToken cancellationToken = default)
    {
        await WrapDockerOperationAsync("EnsureImage", async () =>
        {
            try
            {
                await _client.Images.InspectImageAsync(_config.Image, cancellationToken);
                _logger?.LogInformation("Image {Image} already exists", _config.Image);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger?.LogInformation("Pulling image {Image}...", _config.Image);
                await _client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = _config.Image },
                    null,
                    new Progress<JSONMessage>(m =>
                    {
                        if (!string.IsNullOrEmpty(m.Status))
                        {
                            _logger?.LogDebug("{Status}", m.Status);
                        }
                    }),
                    cancellationToken);
                _logger?.LogInformation("Image {Image} pulled successfully", _config.Image);
            }
        });
    }

    public Task<ContainerInfo> CreateContainerAsync(int? sessionId = null, bool isWarm = false, CancellationToken cancellationToken = default)
    {
        return CreateContainerAsync(sessionId, isWarm, null, null, cancellationToken);
    }

    public async Task<ContainerInfo> CreateContainerAsync(int? sessionId, bool isWarm, ResourceLimits? resourceLimits, NetworkMode? networkMode, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("CreateContainer", async () =>
        {
            // 使用指定的资源限制或默认值
            ResourceLimits limits = resourceLimits ?? _config.DefaultResourceLimits;
            // 验证不超过最大限制
            limits.Validate(_config.MaxResourceLimits);

            // 使用指定的网络模式或默认值
            NetworkMode network = networkMode ?? _config.DefaultNetworkMode;

            var containerName = $"{_config.LabelPrefix}-{Guid.NewGuid():N}";
            var labels = new Dictionary<string, string>
            {
                [$"{_config.LabelPrefix}.managed"] = "true",
                [$"{_config.LabelPrefix}.created"] = DateTimeOffset.UtcNow.ToString("o"),
                [$"{_config.LabelPrefix}.warm"] = isWarm.ToString().ToLower(),
                [$"{_config.LabelPrefix}.memory"] = limits.MemoryBytes.ToString(),
                [$"{_config.LabelPrefix}.cpu"] = limits.CpuCores.ToString("F2"),
                [$"{_config.LabelPrefix}.pids"] = limits.MaxProcesses.ToString(),
                [$"{_config.LabelPrefix}.network"] = network.ToString().ToLower()
            };

            if (sessionId.HasValue)
            {
                labels[$"{_config.LabelPrefix}.session"] = sessionId.Value.ToString();
            }

            // 构建 HostConfig，Windows 容器不支持某些选项
            var hostConfig = new HostConfig
            {
                NetworkMode = network.ToDockerNetworkMode(_config.IsWindowsContainer),
                Memory = limits.MemoryBytes,
                NanoCPUs = (long)(limits.CpuCores * 1_000_000_000) // 1e9 = 1 CPU
            };

            if (!_config.IsWindowsContainer)
            {
                // Linux 容器：支持 PidsLimit
                hostConfig.PidsLimit = limits.MaxProcesses;
            }
            else
            {
                // Windows 容器：Windows Server 2025 支持 Memory 和 CPU 限制
                // 但不支持 PidsLimit（这是 Linux cgroups 特有功能）
                _logger?.LogDebug("Windows container mode: PidsLimit is not supported");
            }

            CreateContainerResponse response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = containerName,
                Image = _config.Image,
                Tty = false,
                AttachStdout = false,
                AttachStderr = false,
                Cmd = _config.GetKeepAliveCommand(),
                WorkingDir = _config.WorkDir,
                Labels = labels,
                HostConfig = hostConfig
            }, cancellationToken);

            await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters(), cancellationToken);

            _logger?.LogInformation("Created and started container {ContainerId} (name: {Name}, warm: {IsWarm}, memory: {Memory}MB, cpu: {Cpu}, pids: {Pids}, network: {Network})",
                response.ID[..12], containerName, isWarm,
                limits.MemoryBytes / 1024 / 1024, limits.CpuCores, limits.MaxProcesses, network);

            // 创建工作目录和 artifacts 目录
            var mkdirCmd = _config.GetMkdirCommand(_config.WorkDir, $"{_config.WorkDir}/{_config.ArtifactsDir}");
            await ExecuteCommandAsync(response.ID, mkdirCmd, "/", 30, cancellationToken);

            return new ContainerInfo
            {
                ContainerId = response.ID,
                Name = containerName,
                Image = _config.Image,
                DockerStatus = "running",
                Status = SdkContainerStatus.Warming,
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
                SessionId = sessionId,
                Labels = labels
            };
        });
    }

    public async Task<List<ContainerInfo>> GetManagedContainersAsync(CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("GetManagedContainers", async () =>
        {
            IList<ContainerListResponse> containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{_config.LabelPrefix}.managed=true"] = true
                    }
                }
            }, cancellationToken);

            var result = new List<ContainerInfo>();
            foreach (ContainerListResponse? container in containers)
            {
                container.Labels.TryGetValue($"{_config.LabelPrefix}.session", out var sessionIdStr);
                int? containerSessionId = int.TryParse(sessionIdStr, out var sid) ? sid : null;

                result.Add(new ContainerInfo
                {
                    ContainerId = container.ID,
                    Name = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12],
                    Image = container.Image,
                    DockerStatus = container.State,
                    Status = containerSessionId == null ? SdkContainerStatus.Idle : SdkContainerStatus.Busy,
                    CreatedAt = container.Created,
                    SessionId = containerSessionId,
                    Labels = new Dictionary<string, string>(container.Labels)
                });
            }

            return result;
        });
    }

    public async Task<ContainerInfo?> GetContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("GetContainer", async () =>
        {
            try
            {
                ContainerInspectResponse inspect = await _client.Containers.InspectContainerAsync(containerId, cancellationToken);

                if (!inspect.Config.Labels.TryGetValue($"{_config.LabelPrefix}.managed", out var managed) || managed != "true")
                {
                    return null;
                }

                inspect.Config.Labels.TryGetValue($"{_config.LabelPrefix}.session", out var sessionIdStr);
                int? containerSessionId = int.TryParse(sessionIdStr, out var sid) ? sid : null;

                return new ContainerInfo
                {
                    ContainerId = inspect.ID,
                    Name = inspect.Name.TrimStart('/'),
                    Image = inspect.Config.Image,
                    DockerStatus = inspect.State.Status,
                    Status = containerSessionId == null ? SdkContainerStatus.Idle : SdkContainerStatus.Busy,
                    CreatedAt = inspect.Created,
                    StartedAt = DateTimeOffset.TryParse(inspect.State.StartedAt, out DateTimeOffset started) ? started : null,
                    SessionId = containerSessionId,
                    Labels = new Dictionary<string, string>(inspect.Config.Labels)
                };
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }, containerId);
    }

    public async Task DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        await WrapDockerOperationAsync("DeleteContainer", async () =>
        {
            try
            {
                await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }, cancellationToken);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger?.LogWarning("Container {ContainerId} not found, skipping delete", containerId[..Math.Min(12, containerId.Length)]);
                return;
            }
            catch (DockerApiException ex)
            {
                _logger?.LogWarning("Failed to stop container: {StatusCode}", ex.StatusCode);
            }

            try
            {
                await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, cancellationToken);
                _logger?.LogInformation("Deleted container {ContainerId}", containerId[..Math.Min(12, containerId.Length)]);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger?.LogWarning("Container {ContainerId} already removed", containerId[..Math.Min(12, containerId.Length)]);
            }
        }, containerId);
    }

    public async Task DeleteAllManagedContainersAsync(CancellationToken cancellationToken = default)
    {
        List<ContainerInfo> containers = await GetManagedContainersAsync(cancellationToken);
        foreach (ContainerInfo container in containers)
        {
            await DeleteContainerAsync(container.ContainerId, cancellationToken);
        }
        _logger?.LogInformation("Deleted {Count} managed containers", containers.Count);
    }

    public Task<CommandResult> ExecuteCommandAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        // 包装为 shell 命令
        return ExecuteCommandAsync(containerId, _config.GetShellCommand(command), workingDirectory, timeoutSeconds, cancellationToken);
    }

    public async Task<CommandResult> ExecuteCommandAsync(string containerId, string[] command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("ExecuteCommand", async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            ContainerExecCreateResponse execCreate = await _client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                WorkingDir = workingDirectory,
                Cmd = command
            }, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using MultiplexedStream stream = await _client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, tty: false, cts.Token);
            (string? stdout, string? stderr) = await ReadOutputAsync(stream, cts.Token);

            ContainerExecInspectResponse inspect = await _client.Exec.InspectContainerExecAsync(execCreate.ID, cancellationToken);

            sw.Stop();

            // 应用输出截断
            (string? truncatedStdout, bool stdoutTruncated) = TruncateOutput(stdout);
            (string? truncatedStderr, bool stderrTruncated) = TruncateOutput(stderr);

            return new CommandResult
            {
                Stdout = truncatedStdout,
                Stderr = truncatedStderr,
                ExitCode = inspect.ExitCode,
                ExecutionTimeMs = sw.ElapsedMilliseconds,
                IsTruncated = stdoutTruncated || stderrTruncated
            };
        }, containerId);
    }

    public IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(
        string containerId,
        string command,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 包装为 shell 命令
        return ExecuteCommandStreamAsync(containerId, _config.GetShellCommand(command), workingDirectory, timeoutSeconds, cancellationToken);
    }

    public async IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(
        string containerId,
        string[] command,
        string workingDirectory,
        int timeoutSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? execId = null;

        ContainerInfo? container = await GetContainerAsync(containerId, cancellationToken);
        if (container == null)
        {
            throw new ContainerNotFoundException(containerId);
        }

        ContainerExecCreateResponse execCreate = await _client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = workingDirectory,
            Cmd = command
        }, cancellationToken);

        execId = execCreate.ID;
        _logger?.LogDebug("Created exec instance {ExecId} for container {ContainerId}", execId, containerId[..12]);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        MultiplexedStream? stream = null;
        try
        {
            stream = await _client.Exec.StartAndAttachContainerExecAsync(execId, tty: false, cts.Token);

            var buffer = new byte[4096];

            while (!cts.Token.IsCancellationRequested)
            {
                MultiplexedStream.ReadResult result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);

                if (result.EOF || result.Count == 0)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

                if (result.Target == MultiplexedStream.TargetStream.StandardOut)
                {
                    yield return CommandOutputEvent.FromStdout(text);
                }
                else
                {
                    yield return CommandOutputEvent.FromStderr(text);
                }
            }
        }
        finally
        {
            stream?.Dispose();
        }

        sw.Stop();
        long exitCode = -1;
        try
        {
            ContainerExecInspectResponse inspect = await _client.Exec.InspectContainerExecAsync(execId, CancellationToken.None);
            exitCode = inspect.ExitCode;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to inspect exec {ExecId}", execId);
        }

        yield return CommandOutputEvent.FromExit(exitCode, sw.ElapsedMilliseconds);
    }

    public async Task UploadFileAsync(string containerId, string containerPath, byte[] content, CancellationToken cancellationToken = default)
    {
        await WrapDockerOperationAsync("UploadFile", async () =>
        {
            var relativePath = containerPath.TrimStart('/');
            await using var tarStream = new MemoryStream();
            using (IWriter writer = WriterFactory.Open(tarStream, ArchiveType.Tar, new WriterOptions(CompressionType.None) { LeaveStreamOpen = true }))
            {
                await using var dataStream = new MemoryStream(content);
                writer.Write(relativePath, dataStream, null);
            }
            tarStream.Seek(0, SeekOrigin.Begin);

            await _client.Containers.ExtractArchiveToContainerAsync(
                containerId,
                new ContainerPathStatParameters { Path = "/" },
                tarStream,
                cancellationToken);

            _logger?.LogInformation("Uploaded file to container {ContainerId}: {Path} ({Size} bytes)", containerId[..12], containerPath, content.Length);
        }, containerId);
    }

    public async Task<List<FileEntry>> ListDirectoryAsync(string containerId, string path, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("ListDirectory", async () =>
        {
            var entries = new List<FileEntry>();

            try
            {
                GetArchiveFromContainerResponse archive = await _client.Containers.GetArchiveFromContainerAsync(
                    containerId,
                    new GetArchiveFromContainerParameters { Path = path },
                    false,
                    cancellationToken);

                await using Stream stream = archive.Stream;
                using IReader reader = ReaderFactory.Open(stream);

                while (reader.MoveToNextEntry())
                {
                    IEntry entry = reader.Entry;
                    if (string.IsNullOrWhiteSpace(entry.Key))
                        continue;

                    var cleanKey = entry.Key.TrimStart('.', '/');
                    if (string.IsNullOrEmpty(cleanKey))
                        continue;

                    var fullPath = path.TrimEnd('/') + "/" + cleanKey.TrimEnd('/');
                    if (fullPath.TrimEnd('/') == path.TrimEnd('/'))
                        continue;

                    entries.Add(new FileEntry
                    {
                        Path = fullPath,
                        Name = Path.GetFileName(cleanKey.TrimEnd('/')),
                        IsDirectory = entry.IsDirectory,
                        Size = entry.Size,
                        LastModified = entry.LastModifiedTime
                    });
                }
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger?.LogWarning("Directory not found: {Path}", path);
            }

            return entries;
        }, containerId);
    }

    public async Task<byte[]> DownloadFileAsync(string containerId, string filePath, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("DownloadFile", async () =>
        {
            GetArchiveFromContainerResponse archive = await _client.Containers.GetArchiveFromContainerAsync(
                containerId,
                new GetArchiveFromContainerParameters { Path = filePath },
                false,
                cancellationToken);

            await using Stream stream = archive.Stream;
            using IReader reader = ReaderFactory.Open(stream);

            while (reader.MoveToNextEntry())
            {
                IEntry entry = reader.Entry;
                if (!entry.IsDirectory)
                {
                    await using var memory = new MemoryStream();
                    reader.WriteEntryTo(memory);
                    return memory.ToArray();
                }
            }

            throw new FileNotFoundException($"File {filePath} not found in container");
        }, containerId);
    }

    public async Task AssignSessionToContainerAsync(string containerId, int sessionId, CancellationToken cancellationToken = default)
    {
        await WrapDockerOperationAsync("AssignSession", async () =>
        {
            _logger?.LogInformation("Container {ContainerId} assigned to session {SessionId}", containerId[..12], sessionId);
            
            // Windows Hyper-V 隔离容器不支持文件系统操作，跳过标记文件创建
            // 会话关联信息已通过容器标签存储
            if (_config.IsWindowsContainer)
            {
                _logger?.LogDebug("Skipping session marker file for Windows container (Hyper-V isolation does not support filesystem operations)");
                return;
            }
            
            var marker = $"Session: {sessionId}\nAssigned: {DateTimeOffset.UtcNow:o}";
            await UploadFileAsync(containerId, $"{_config.WorkDir}/.session", Encoding.UTF8.GetBytes(marker), cancellationToken);
        }, containerId);
    }

    public async Task<SessionUsage?> GetContainerStatsAsync(string containerId, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("GetContainerStats", async () =>
        {
            try
            {
                ContainerStatsResponse? statsData = null;
                
                // 使用同步 Action 来捕获数据
                var progress = new Progress<ContainerStatsResponse>(stats =>
                {
                    statsData = stats;
                });

                // 使用新的 API 签名获取容器统计信息（一次性读取）
                await _client.Containers.GetContainerStatsAsync(
                    containerId,
                    new ContainerStatsParameters { Stream = false },
                    progress,
                    cancellationToken);

                // 等待一小段时间确保回调已执行
                await Task.Delay(50, cancellationToken);

                if (statsData == null)
                {
                    _logger?.LogWarning("No stats data received for container {ContainerId}", containerId[..12]);
                    return null;
                }

                var usage = new SessionUsage
                {
                    ContainerId = containerId
                };

                // CPU 使用
                if (statsData.CPUStats?.CPUUsage != null)
                {
                    usage.CpuUsageNanos = (long)statsData.CPUStats.CPUUsage.TotalUsage;
                }

                // 内存使用
                if (statsData.MemoryStats != null)
                {
                    usage.MemoryUsageBytes = (long)statsData.MemoryStats.Usage;
                    usage.PeakMemoryBytes = (long)statsData.MemoryStats.MaxUsage;
                }

                // 网络 IO
                if (statsData.Networks != null)
                {
                    foreach (NetworkStats? network in statsData.Networks.Values)
                    {
                        usage.NetworkRxBytes += (long)network.RxBytes;
                        usage.NetworkTxBytes += (long)network.TxBytes;
                    }
                }

                return usage;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get container stats for {ContainerId}", containerId[..12]);
                return null;
            }
        }, containerId);
    }

    private (string output, bool truncated) TruncateOutput(string output)
    {
        OutputOptions options = _config.OutputOptions;
        var bytes = Encoding.UTF8.GetBytes(output);

        if (bytes.Length <= options.MaxOutputBytes)
        {
            return (output, false);
        }

        var halfSize = options.MaxOutputBytes / 2;
        var omittedBytes = bytes.Length - options.MaxOutputBytes;

        return options.Strategy switch
        {
            TruncationStrategy.Head => (
                Encoding.UTF8.GetString(bytes, 0, options.MaxOutputBytes) +
                string.Format(options.TruncationMessage, omittedBytes),
                true),

            TruncationStrategy.Tail => (
                string.Format(options.TruncationMessage, omittedBytes) +
                Encoding.UTF8.GetString(bytes, bytes.Length - options.MaxOutputBytes, options.MaxOutputBytes),
                true),

            TruncationStrategy.HeadAndTail => (
                Encoding.UTF8.GetString(bytes, 0, halfSize) +
                string.Format(options.TruncationMessage, omittedBytes) +
                Encoding.UTF8.GetString(bytes, bytes.Length - halfSize, halfSize),
                true),

            _ => (output, false)
        };
    }

    private static async Task<(string stdout, string stderr)> ReadOutputAsync(MultiplexedStream stream, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            MultiplexedStream.ReadResult result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
            if (result.EOF || result.Count == 0)
                break;

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (result.Target == MultiplexedStream.TargetStream.StandardOut)
            {
                stdout.Append(text);
            }
            else
            {
                stderr.Append(text);
            }
        }

        return (stdout.ToString(), stderr.ToString());
    }

    private async Task<T> WrapDockerOperationAsync<T>(string operation, Func<Task<T>> action, string? containerId = null)
    {
        try
        {
            return await action();
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && containerId != null)
        {
            _logger?.LogError(ex, "Container {ContainerId} not found", containerId);
            throw new ContainerNotFoundException(containerId, ex);
        }
        catch (DockerApiException ex)
        {
            _logger?.LogError(ex, "Docker API operation {Operation} failed: {StatusCode}", operation, ex.StatusCode);
            throw new DockerOperationException(operation, ex.Message, ex);
        }
        catch (HttpRequestException ex) when (IsDockerConnectionError(ex))
        {
            _logger?.LogError(ex, "Docker connection failed: {Operation}", operation);
            throw new DockerConnectionException($"Unable to connect to Docker service. Please ensure Docker Desktop is running. Operation: {operation}", ex);
        }
        catch (TimeoutException ex)
        {
            _logger?.LogError(ex, "Docker operation timed out: {Operation}", operation);
            throw new DockerConnectionException($"Docker operation timed out. Operation: {operation}", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ContainerNotFoundException && ex is not DockerConnectionException && ex is not DockerOperationException)
        {
            _logger?.LogError(ex, "Docker operation failed unexpectedly: {Operation}", operation);
            throw new DockerOperationException(operation, ex.Message, ex);
        }
    }

    private async Task WrapDockerOperationAsync(string operation, Func<Task> action, string? containerId = null)
    {
        await WrapDockerOperationAsync(operation, async () =>
        {
            await action();
            return true;
        }, containerId);
    }

    private static bool IsDockerConnectionError(Exception ex)
    {
        var message = ex.Message.ToLower();
        return message.Contains("no connection") ||
               message.Contains("connection refused") ||
               message.Contains("unable to connect") ||
               message.Contains("pipe") ||
               message.Contains("socket") ||
               message.Contains("docker") ||
               ex.InnerException is IOException;
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
