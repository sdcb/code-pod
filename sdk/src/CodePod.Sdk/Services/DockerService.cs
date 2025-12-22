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
    /// 创建并启动容器
    /// </summary>
    Task<ContainerInfo> CreateContainerAsync(string? sessionId = null, bool isWarm = false, CancellationToken cancellationToken = default);

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
    /// 执行命令
    /// </summary>
    Task<CommandResult> ExecuteCommandAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式执行命令
    /// </summary>
    IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default);

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
    Task AssignSessionToContainerAsync(string containerId, string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Docker服务实现
/// </summary>
public class DockerService : IDockerService
{
    private readonly DockerClient _client;
    private readonly CodePodConfig _config;
    private readonly ILogger<DockerService>? _logger;

    public DockerService(IDockerClientFactory clientFactory, CodePodConfig config, ILogger<DockerService>? logger = null)
    {
        _client = clientFactory.CreateClient();
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

    public async Task<ContainerInfo> CreateContainerAsync(string? sessionId = null, bool isWarm = false, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("CreateContainer", async () =>
        {
            var containerName = $"{_config.LabelPrefix}-{Guid.NewGuid():N}";
            var labels = new Dictionary<string, string>
            {
                [$"{_config.LabelPrefix}.managed"] = "true",
                [$"{_config.LabelPrefix}.created"] = DateTimeOffset.UtcNow.ToString("o"),
                [$"{_config.LabelPrefix}.warm"] = isWarm.ToString().ToLower()
            };

            if (!string.IsNullOrEmpty(sessionId))
            {
                labels[$"{_config.LabelPrefix}.session"] = sessionId;
            }

            var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = containerName,
                Image = _config.Image,
                Tty = false,
                AttachStdout = false,
                AttachStderr = false,
                Cmd = ["/bin/bash", "-lc", "tail -f /dev/null"],
                WorkingDir = _config.WorkDir,
                Labels = labels,
                HostConfig = new HostConfig
                {
                    Memory = 512 * 1024 * 1024, // 512MB
                    CPUPercent = 50,
                    NetworkMode = "bridge"
                }
            }, cancellationToken);

            await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters(), cancellationToken);

            _logger?.LogInformation("Created and started container {ContainerId} (name: {Name}, warm: {IsWarm})", response.ID[..12], containerName, isWarm);

            // 创建工作目录
            await ExecuteCommandAsync(response.ID, $"mkdir -p {_config.WorkDir}", "/", 10, cancellationToken);

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
            var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
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
            foreach (var container in containers)
            {
                container.Labels.TryGetValue($"{_config.LabelPrefix}.session", out var containerSessionId);

                result.Add(new ContainerInfo
                {
                    ContainerId = container.ID,
                    Name = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12],
                    Image = container.Image,
                    DockerStatus = container.State,
                    Status = string.IsNullOrEmpty(containerSessionId) ? SdkContainerStatus.Idle : SdkContainerStatus.Busy,
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
                var inspect = await _client.Containers.InspectContainerAsync(containerId, cancellationToken);

                if (!inspect.Config.Labels.TryGetValue($"{_config.LabelPrefix}.managed", out var managed) || managed != "true")
                {
                    return null;
                }

                inspect.Config.Labels.TryGetValue($"{_config.LabelPrefix}.session", out var containerSessionId);

                return new ContainerInfo
                {
                    ContainerId = inspect.ID,
                    Name = inspect.Name.TrimStart('/'),
                    Image = inspect.Config.Image,
                    DockerStatus = inspect.State.Status,
                    Status = string.IsNullOrEmpty(containerSessionId) ? SdkContainerStatus.Idle : SdkContainerStatus.Busy,
                    CreatedAt = inspect.Created,
                    StartedAt = DateTimeOffset.TryParse(inspect.State.StartedAt, out var started) ? started : null,
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
        var containers = await GetManagedContainersAsync(cancellationToken);
        foreach (var container in containers)
        {
            await DeleteContainerAsync(container.ContainerId, cancellationToken);
        }
        _logger?.LogInformation("Deleted {Count} managed containers", containers.Count);
    }

    public async Task<CommandResult> ExecuteCommandAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        return await WrapDockerOperationAsync("ExecuteCommand", async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var execCreate = await _client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                WorkingDir = workingDirectory,
                Cmd = ["/bin/bash", "-lc", command]
            }, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var stream = await _client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, tty: false, cts.Token);
            var (stdout, stderr) = await ReadOutputAsync(stream, cts.Token);

            var inspect = await _client.Exec.InspectContainerExecAsync(execCreate.ID, cancellationToken);

            sw.Stop();

            return new CommandResult
            {
                Stdout = stdout,
                Stderr = stderr,
                ExitCode = inspect.ExitCode,
                ExecutionTimeMs = sw.ElapsedMilliseconds
            };
        }, containerId);
    }

    public async IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(
        string containerId,
        string command,
        string workingDirectory,
        int timeoutSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? execId = null;

        var container = await GetContainerAsync(containerId, cancellationToken);
        if (container == null)
        {
            throw new ContainerNotFoundException(containerId);
        }

        var execCreate = await _client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = workingDirectory,
            Cmd = ["/bin/bash", "-lc", command]
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
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);

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
            var inspect = await _client.Exec.InspectContainerExecAsync(execId, CancellationToken.None);
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
            using (var writer = WriterFactory.Open(tarStream, ArchiveType.Tar, new WriterOptions(CompressionType.None) { LeaveStreamOpen = true }))
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
                var archive = await _client.Containers.GetArchiveFromContainerAsync(
                    containerId,
                    new GetArchiveFromContainerParameters { Path = path },
                    false,
                    cancellationToken);

                await using var stream = archive.Stream;
                using var reader = ReaderFactory.Open(stream);

                while (reader.MoveToNextEntry())
                {
                    var entry = reader.Entry;
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
            var archive = await _client.Containers.GetArchiveFromContainerAsync(
                containerId,
                new GetArchiveFromContainerParameters { Path = filePath },
                false,
                cancellationToken);

            await using var stream = archive.Stream;
            using var reader = ReaderFactory.Open(stream);

            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
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

    public async Task AssignSessionToContainerAsync(string containerId, string sessionId, CancellationToken cancellationToken = default)
    {
        await WrapDockerOperationAsync("AssignSession", async () =>
        {
            _logger?.LogInformation("Container {ContainerId} assigned to session {SessionId}", containerId[..12], sessionId);
            var marker = $"Session: {sessionId}\nAssigned: {DateTimeOffset.UtcNow:o}";
            await UploadFileAsync(containerId, "/app/.session", Encoding.UTF8.GetBytes(marker), cancellationToken);
        }, containerId);
    }

    private static async Task<(string stdout, string stderr)> ReadOutputAsync(MultiplexedStream stream, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
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
