#:package Docker.DotNet@3.125.14

// Docker 流式命令执行实验
// 使用 .NET 10 single-file runner 语法。执行命令: dotnet run DockerStreamingExec.cs

using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

var image = "mcr.microsoft.com/dotnet/sdk:10.0";
using var client = CreateDockerClient();

Console.WriteLine("🔌 Connecting to Docker Engine...");
await EnsureImageAsync(client, image);

var containerName = $"exp-streaming-{Guid.NewGuid():N}";
var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
{
    Name = containerName,
    Image = image,
    Tty = false,
    AttachStdout = false,
    AttachStderr = false,
    Cmd = ["/bin/bash", "-lc", "tail -f /dev/null"],
    Labels = new Dictionary<string, string>
    {
        ["exp.module"] = "DockerStreamingExec",
        ["exp.owner"] = Environment.UserName
    }
});

Console.WriteLine($"✅ Created container {createResponse.ID[..12]} ({containerName}).");

await client.Containers.StartContainerAsync(createResponse.ID, new ContainerStartParameters());
Console.WriteLine("▶️ Started container.");

// ============================================================================
// 实验 1: 流式获取命令输出（模拟长时间运行的脚本）
// ============================================================================
Console.WriteLine("\n📌 实验 1: 流式获取命令输出");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

var streamingCommand = "for i in 1 2 3 4 5; do echo \"stdout: Message $i\"; echo \"stderr: Warning $i\" >&2; sleep 0.5; done; echo 'Done!'";

Console.WriteLine($"执行命令: {streamingCommand}");
Console.WriteLine();

var execCreate = await client.Exec.ExecCreateContainerAsync(createResponse.ID, new ContainerExecCreateParameters
{
    AttachStdout = true,
    AttachStderr = true,
    Cmd = ["/bin/bash", "-lc", streamingCommand]
});

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var stream = await client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, tty: false, cts.Token);

Console.WriteLine("🔄 开始流式读取输出:");
Console.WriteLine("-----------------------------------------------------------");

await foreach (var (target, data) in StreamOutputAsync(stream, cts.Token))
{
    var prefix = target == MultiplexedStream.TargetStream.StandardOut ? "[stdout]" : "[stderr]";
    var color = target == MultiplexedStream.TargetStream.StandardOut ? ConsoleColor.White : ConsoleColor.Yellow;
    
    Console.ForegroundColor = color;
    Console.Write($"{prefix} ");
    Console.ResetColor();
    Console.Write(data);
}

Console.WriteLine("-----------------------------------------------------------");

var inspect = await client.Exec.InspectContainerExecAsync(execCreate.ID);
Console.WriteLine($"\n✅ 命令执行完成，退出码: {inspect.ExitCode}");

// ============================================================================
// 实验 2: 测试超时取消
// ============================================================================
Console.WriteLine("\n📌 实验 2: 测试超时取消");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

var longRunningCommand = "for i in $(seq 1 100); do echo \"Line $i\"; sleep 0.3; done";
Console.WriteLine($"执行命令 (2秒超时): {longRunningCommand}");

var execCreate2 = await client.Exec.ExecCreateContainerAsync(createResponse.ID, new ContainerExecCreateParameters
{
    AttachStdout = true,
    AttachStderr = true,
    Cmd = ["/bin/bash", "-lc", longRunningCommand]
});

using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
using var stream2 = await client.Exec.StartAndAttachContainerExecAsync(execCreate2.ID, tty: false, CancellationToken.None);

Console.WriteLine("🔄 开始流式读取 (会在2秒后取消):");

try
{
    await foreach (var (target, data) in StreamOutputAsync(stream2, cts2.Token))
    {
        Console.Write(data);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n⏱️ 命令执行超时，已取消");
}

// ============================================================================
// 实验 3: 测试错误命令
// ============================================================================
Console.WriteLine("\n📌 实验 3: 测试错误命令");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

var errorCommand = "echo 'Starting...'; invalid_command_xyz; echo 'This should still run'";
Console.WriteLine($"执行命令: {errorCommand}");

var execCreate3 = await client.Exec.ExecCreateContainerAsync(createResponse.ID, new ContainerExecCreateParameters
{
    AttachStdout = true,
    AttachStderr = true,
    Cmd = ["/bin/bash", "-lc", errorCommand]
});

using var stream3 = await client.Exec.StartAndAttachContainerExecAsync(execCreate3.ID, tty: false, CancellationToken.None);

await foreach (var (target, data) in StreamOutputAsync(stream3, CancellationToken.None))
{
    var prefix = target == MultiplexedStream.TargetStream.StandardOut ? "[stdout]" : "[stderr]";
    Console.Write($"{prefix} {data}");
}

var inspect3 = await client.Exec.InspectContainerExecAsync(execCreate3.ID);
Console.WriteLine($"退出码: {inspect3.ExitCode}");

// ============================================================================
// 清理
// ============================================================================
Console.WriteLine("\n🧹 Cleaning up...");
await client.Containers.StopContainerAsync(createResponse.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }, CancellationToken.None);
await client.Containers.RemoveContainerAsync(createResponse.ID, new ContainerRemoveParameters { Force = true });
Console.WriteLine("Done. Experiment succeeded.");

// ============================================================================
// Helper Methods
// ============================================================================

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

/// <summary>
/// 流式读取 Docker exec 输出的核心方法
/// </summary>
static async IAsyncEnumerable<(MultiplexedStream.TargetStream Target, string Data)> StreamOutputAsync(
    MultiplexedStream stream,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    var buffer = new byte[4096];
    
    while (!cancellationToken.IsCancellationRequested)
    {
        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
        
        if (result.EOF || result.Count == 0)
        {
            break;
        }
        
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        yield return (result.Target, text);
    }
}
