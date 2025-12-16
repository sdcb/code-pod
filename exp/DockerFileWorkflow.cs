#:package Docker.DotNet@3.125.14
#:package SharpCompress@0.37.2

// Demonstrates file IO (upload/list/download) against containers without invoking the docker CLI.
// Run via `dotnet run DockerFileWorkflow.cs`.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docker.DotNet;
using Docker.DotNet.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

var image = "mcr.microsoft.com/dotnet/sdk:10.0";
using var client = CreateDockerClient();
await EnsureImageAsync(client, image);

var containerName = $"exp-files-{Guid.NewGuid():N}";
var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
{
    Name = containerName,
    Image = image,
    Cmd = new[] { "/bin/bash", "-lc", "tail -f /dev/null" },
    Labels = new Dictionary<string, string>
    {
        ["exp.module"] = "DockerFileWorkflow"
    }
});

await client.Containers.StartContainerAsync(createResponse.ID, new ContainerStartParameters());
Console.WriteLine($"Container {createResponse.ID[..12]} is ready.");

var samplePayload = new SamplePayload
{
    Prompt = "Summarize the latest commit",
    Files = ["report.md", "metrics.json"],
    RequestedBy = Environment.UserName,
    Timestamp = DateTimeOffset.UtcNow
};

await UploadAsciiFileAsync(client, createResponse.ID, "/app/input/session-request.json", JsonSerializer.Serialize(samplePayload, AppJsonContext.Default.SamplePayload));
Console.WriteLine("Uploaded session-request.json to /app/input.");

var entries = await ListDirectoryAsync(client, createResponse.ID, "/app");
Console.WriteLine("Directory snapshot of /app (via Docker archive API):");
foreach (var entry in entries)
{
    Console.WriteLine($" - {entry.Path} ({entry.Kind}, {entry.Size} bytes)");
}

var commandOutput = await RunCommandAsync(client, createResponse.ID, "mkdir -p /app/output && dotnet --info > /app/output/dotnet-info.txt && ls -R /app");
Console.WriteLine("Command output:");
Console.WriteLine(commandOutput);

var downloaded = await DownloadFileAsync(client, createResponse.ID, "/app/output/dotnet-info.txt");
var hostPath = Path.Combine(Path.GetTempPath(), $"dotnet-info-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.txt");
await File.WriteAllBytesAsync(hostPath, downloaded);
Console.WriteLine($"Downloaded dotnet-info.txt to {hostPath}");
Console.WriteLine("Content preview:");
Console.WriteLine(Encoding.UTF8.GetString(downloaded)[..Math.Min(400, downloaded.Length)]);

await client.Containers.StopContainerAsync(createResponse.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 2 });
await client.Containers.RemoveContainerAsync(createResponse.ID, new ContainerRemoveParameters { Force = true });
Console.WriteLine("Cleanup complete.");

static DockerClient CreateDockerClient()
{
    var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
    if (!string.IsNullOrWhiteSpace(dockerHost))
    {
        return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
    }

    var uri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "npipe://./pipe/docker_engine"
        : "unix:///var/run/docker.sock";

    return new DockerClientConfiguration(new Uri(uri)).CreateClient();
}

static async Task EnsureImageAsync(DockerClient client, string image)
{
    try
    {
        await client.Images.InspectImageAsync(image);
    }
    catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.WriteLine($"Pulling image {image}...");
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image },
            null,
            new Progress<JSONMessage>(m =>
            {
                if (!string.IsNullOrEmpty(m.Status))
                {
                    Console.WriteLine(m.Status);
                }
            }));
    }
}

static async Task UploadAsciiFileAsync(DockerClient client, string containerId, string containerPath, string content, CancellationToken cancellationToken = default)
{
    var relativePath = containerPath.TrimStart('/');
    await using var tarStream = new MemoryStream();
    using (var writer = WriterFactory.Open(tarStream, ArchiveType.Tar, new WriterOptions(CompressionType.None) { LeaveStreamOpen = true }))
    {
        await using var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        writer.Write(relativePath, dataStream, DateTime.UtcNow);
    }
    tarStream.Seek(0, SeekOrigin.Begin);

    await client.Containers.ExtractArchiveToContainerAsync(
        containerId,
        new ContainerPathStatParameters { Path = "/" },
        tarStream,
        cancellationToken);
}

static async Task<IReadOnlyList<DirectoryEntry>> ListDirectoryAsync(DockerClient client, string containerId, string path, CancellationToken cancellationToken = default)
{
    var entries = new List<DirectoryEntry>();
    var archive = await client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters { Path = path }, false, cancellationToken);
    await using var stream = archive.Stream;
    using var reader = ReaderFactory.Open(stream);
    while (reader.MoveToNextEntry())
    {
        var entry = reader.Entry;
        if (string.IsNullOrWhiteSpace(entry.Key))
        {
            continue;
        }

        var cleanName = entry.Key.TrimStart('.', '/');
        if (string.IsNullOrEmpty(cleanName))
        {
            continue;
        }

        entries.Add(new DirectoryEntry(
            Path: "/" + cleanName.Replace('\\', '/'),
            Kind: entry.IsDirectory ? "Directory" : "File",
            Size: entry.Size));
    }

    return entries;
}

static async Task<byte[]> DownloadFileAsync(DockerClient client, string containerId, string filePath, CancellationToken cancellationToken = default)
{
    var archive = await client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters { Path = filePath }, false, cancellationToken);
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

    throw new FileNotFoundException($"File {filePath} not found inside container.");
}

static async Task<string> RunCommandAsync(DockerClient client, string containerId, string command, CancellationToken cancellationToken = default)
{
    var execCreate = await client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
    {
        AttachStdout = true,
        AttachStderr = true,
        Cmd = new[] { "/bin/bash", "-lc", command }
    }, cancellationToken);

    using var stream = await client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, tty: false, cancellationToken);
    var (stdout, stderr) = await ReadOutputAsync(stream, cancellationToken);
    if (!string.IsNullOrEmpty(stderr))
    {
        return stdout + Environment.NewLine + "stderr:" + Environment.NewLine + stderr;
    }

    return stdout;
}

static async Task<(string stdout, string stderr)> ReadOutputAsync(MultiplexedStream stream, CancellationToken cancellationToken)
{
    var stdout = new StringBuilder();
    var stderr = new StringBuilder();
    var buffer = new byte[4096];

    while (true)
    {
        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
        if (result.EOF || result.Count == 0)
        {
            break;
        }

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

record DirectoryEntry(string Path, string Kind, long Size);

record SamplePayload
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }
    
    [JsonPropertyName("files")]
    public required string[] Files { get; init; }
    
    [JsonPropertyName("requestedBy")]
    public required string RequestedBy { get; init; }
    
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }
}

[JsonSerializable(typeof(SamplePayload))]
[JsonSourceGenerationOptions(WriteIndented = true)]
partial class AppJsonContext : JsonSerializerContext { }
