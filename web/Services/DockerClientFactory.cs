using System.Runtime.InteropServices;
using Docker.DotNet;

namespace DockerShellHost.Services;

/// <summary>
/// Docker客户端工厂
/// </summary>
public interface IDockerClientFactory
{
    DockerClient CreateClient();
}

/// <summary>
/// Docker客户端工厂实现
/// </summary>
public class DockerClientFactory : IDockerClientFactory
{
    public DockerClient CreateClient()
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
}
