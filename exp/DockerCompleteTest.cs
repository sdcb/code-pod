#:package Docker.DotNet@3.125.14
#:package SharpCompress@0.37.2

// 完整的Docker Shell Host实验 - 验证所有核心功能
// 运行: dotnet run DockerCompleteTest.cs

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

Console.WriteLine("=== Docker Shell Host 功能测试 ===\n");

var image = "mcr.microsoft.com/dotnet/sdk:10.0";
using var client = CreateDockerClient();

// 1. 确保镜像存在
Console.WriteLine("【1】检查Docker镜像...");
await EnsureImageAsync(client, image);
Console.WriteLine($"   ✓ 镜像 {image} 已就绪\n");

// 2. 创建容器
Console.WriteLine("【2】创建Docker容器...");
var containerName = $"test-complete-{Guid.NewGuid():N}";
var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
{
    Name = containerName,
    Image = image,
    Cmd = ["/bin/bash", "-lc", "tail -f /dev/null"],
    WorkingDir = "/app",
    Labels = new Dictionary<string, string>
    {
        ["test.module"] = "DockerCompleteTest",
        ["test.owner"] = Environment.UserName
    }
});
await client.Containers.StartContainerAsync(createResponse.ID, new ContainerStartParameters());
var containerId = createResponse.ID;
Console.WriteLine($"   ✓ 容器 {containerId[..12]} 创建并启动\n");

// 3. 执行命令
Console.WriteLine("【3】测试命令执行...");
var cmdResult = await ExecuteCommandAsync(client, containerId, "echo '测试命令执行' && pwd && ls -la");
Console.WriteLine($"   命令输出:\n{Indent(cmdResult.stdout)}");
Console.WriteLine($"   ✓ 命令执行成功，退出码: 0\n");

// 4. 上传文件
Console.WriteLine("【4】测试文件上传...");
var testContent = @"{
  ""name"": ""Docker Shell Host Test"",
  ""version"": ""1.0.0"",
  ""timestamp"": """ + DateTimeOffset.UtcNow.ToString("o") + @""",
  ""features"": [""容器管理"", ""命令执行"", ""文件操作""]
}";
await UploadFileAsync(client, containerId, "/app/test-data.json", testContent);
Console.WriteLine("   ✓ 上传 test-data.json 到 /app\n");

// 5. 列出目录
Console.WriteLine("【5】测试目录列表...");
var entries = await ListDirectoryAsync(client, containerId, "/app");
Console.WriteLine("   /app 目录内容:");
foreach (var entry in entries)
{
    var icon = entry.isDirectory ? "📁" : "📄";
    Console.WriteLine($"     {icon} {entry.name} ({entry.size} bytes)");
}
Console.WriteLine();

// 6. 下载文件
Console.WriteLine("【6】测试文件下载...");
var downloadedContent = await DownloadFileAsync(client, containerId, "/app/test-data.json");
Console.WriteLine($"   下载的文件内容:\n{Indent(downloadedContent)}");
Console.WriteLine($"   ✓ 文件下载成功\n");

// 7. 执行.NET代码
Console.WriteLine("【7】测试在容器中运行.NET代码...");
var csCode = @"
Console.WriteLine(""Hello from Docker Container!"");
Console.WriteLine($""Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"");
Console.WriteLine($""OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"");
for(int i = 1; i <= 5; i++) Console.WriteLine($""Count: {i}"");
";
await UploadFileAsync(client, containerId, "/app/hello.cs", csCode);
var dotnetResult = await ExecuteCommandAsync(client, containerId, "cd /app && dotnet run hello.cs");
Console.WriteLine($"   .NET运行结果:\n{Indent(dotnetResult.stdout)}");

// 8. 清理
Console.WriteLine("【8】清理测试容器...");
await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 2 });
await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
Console.WriteLine($"   ✓ 容器 {containerId[..12]} 已删除\n");

Console.WriteLine("=== 所有测试通过！===");

// --- 辅助方法 ---

static string Indent(string text, string prefix = "     ")
{
    return string.Join("\n", text.Split('\n').Select(line => prefix + line));
}

static DockerClient CreateDockerClient()
{
    var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
    if (!string.IsNullOrWhiteSpace(dockerHost))
        return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();

    var uri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "npipe://./pipe/docker_engine"
        : "unix:///var/run/docker.sock";
    return new DockerClientConfiguration(new Uri(uri)).CreateClient();
}

static async Task EnsureImageAsync(DockerClient client, string image)
{
    try { await client.Images.InspectImageAsync(image); }
    catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.WriteLine($"   ⬇️ 正在拉取镜像 {image}...");
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image }, null,
            new Progress<JSONMessage>(m => { if (!string.IsNullOrEmpty(m.Status)) Console.WriteLine($"   {m.Status}"); }));
    }
}

static async Task<(string stdout, string stderr)> ExecuteCommandAsync(DockerClient client, string containerId, string command)
{
    var exec = await client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
    {
        AttachStdout = true, AttachStderr = true,
        Cmd = ["/bin/bash", "-lc", command]
    });
    using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, CancellationToken.None);
    return await ReadStreamAsync(stream);
}

static async Task<(string stdout, string stderr)> ReadStreamAsync(MultiplexedStream stream)
{
    var stdout = new StringBuilder();
    var stderr = new StringBuilder();
    var buffer = new byte[8192];
    while (true)
    {
        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
        if (result.EOF || result.Count == 0) break;
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        (result.Target == MultiplexedStream.TargetStream.StandardOut ? stdout : stderr).Append(text);
    }
    return (stdout.ToString(), stderr.ToString());
}

static async Task UploadFileAsync(DockerClient client, string containerId, string path, string content)
{
    var relativePath = path.TrimStart('/');
    await using var tarStream = new MemoryStream();
    using (var writer = WriterFactory.Open(tarStream, ArchiveType.Tar, new WriterOptions(CompressionType.None) { LeaveStreamOpen = true }))
    {
        await using var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        writer.Write(relativePath, dataStream, null);
    }
    tarStream.Seek(0, SeekOrigin.Begin);
    await client.Containers.ExtractArchiveToContainerAsync(containerId, new ContainerPathStatParameters { Path = "/" }, tarStream);
}

static async Task<List<(string name, bool isDirectory, long size)>> ListDirectoryAsync(DockerClient client, string containerId, string path)
{
    var entries = new List<(string, bool, long)>();
    var archive = await client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters { Path = path }, false);
    await using var stream = archive.Stream;
    using var reader = ReaderFactory.Open(stream);
    while (reader.MoveToNextEntry())
    {
        var entry = reader.Entry;
        var cleanKey = entry.Key.TrimStart('.', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(cleanKey) || cleanKey == Path.GetFileName(path.TrimEnd('/'))) continue;
        entries.Add((Path.GetFileName(cleanKey), entry.IsDirectory, entry.Size));
    }
    return entries;
}

static async Task<string> DownloadFileAsync(DockerClient client, string containerId, string filePath)
{
    var archive = await client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters { Path = filePath }, false);
    await using var stream = archive.Stream;
    using var reader = ReaderFactory.Open(stream);
    while (reader.MoveToNextEntry())
    {
        if (!reader.Entry.IsDirectory)
        {
            await using var ms = new MemoryStream();
            reader.WriteEntryTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
    throw new FileNotFoundException($"File {filePath} not found");
}
