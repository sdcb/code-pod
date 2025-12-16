#:package Docker.DotNet@3.125.14

// Uses .NET 10 single-file runner syntax. Execute with `dotnet run DockerBasics.cs`.

using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

var image = "mcr.microsoft.com/dotnet/sdk:10.0";
using var client = CreateDockerClient();

Console.WriteLine("🔌 Connecting to Docker Engine...");
await EnsureImageAsync(client, image);

var containerName = $"exp-basic-{Guid.NewGuid():N}";
var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
{
    Name = containerName,
    Image = image,
    Tty = false,
    AttachStdout = false,
    AttachStderr = false,
    Cmd = new[] { "/bin/bash", "-lc", "tail -f /dev/null" },
    Labels = new Dictionary<string, string>
    {
        ["exp.module"] = "DockerBasics",
        ["exp.owner"] = Environment.UserName
    }
});

Console.WriteLine($"✅ Created container {createResponse.ID[..12]} ({containerName}).");

await client.Containers.StartContainerAsync(createResponse.ID, new ContainerStartParameters());
Console.WriteLine("▶️ Started container. Running smoke-test command...");

var execCreate = await client.Exec.ExecCreateContainerAsync(createResponse.ID, new ContainerExecCreateParameters
{
    AttachStdout = true,
    AttachStderr = true,
    Cmd = new[]
    {
        "/bin/bash", "-lc",
        "echo DOTNET_INFO && dotnet --info | head -n 5 && echo PROCESS_LIST && ps -ef | head -n 5"
    }
});

using var stream = await client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, tty: false, CancellationToken.None);
var (stdout, stderr) = await ReadOutputAsync(stream);
Console.WriteLine(stdout);
if (!string.IsNullOrWhiteSpace(stderr))
{
    Console.WriteLine("stderr:");
    Console.WriteLine(stderr);
}

Console.WriteLine("📋 Inspecting container metadata tracked in Docker...");
var inspect = await client.Containers.InspectContainerAsync(createResponse.ID);
Console.WriteLine($"State: {inspect.State.Status} | StartedAt: {inspect.State.StartedAt} | Image: {inspect.Image}");

Console.WriteLine("🧹 Cleaning up (stop + remove)...");
await client.Containers.StopContainerAsync(createResponse.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }, CancellationToken.None);
await client.Containers.RemoveContainerAsync(createResponse.ID, new ContainerRemoveParameters { Force = true });
Console.WriteLine("Done. Experiment succeeded.");

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
        Console.WriteLine($"📦 Image {image} already present.");
        return;
    }
    catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.WriteLine($"⬇️ Pulling image {image}...");
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image },
            null,
            new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message.Status))
                {
                    Console.WriteLine(message.Status);
                }
            }));
        Console.WriteLine("Image pull complete.");
    }
}

static async Task<(string stdout, string stderr)> ReadOutputAsync(MultiplexedStream stream)
{
    var stdout = new StringBuilder();
    var stderr = new StringBuilder();
    var buffer = new byte[8192];

    while (true)
    {
        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
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
