#:package Docker.DotNet@3.125.14

// Simulates a light-weight pre-warmed Docker pool with queueing.
// Run via `dotnet run DockerPoolSimulation.cs`.

using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

var options = new PoolOptions
{
    Image = "mcr.microsoft.com/dotnet/sdk:10.0",
    Prewarm = 2,
    MaxContainers = 3
};

using var client = CreateDockerClient();
var simulator = new DockerPoolSimulator(client, options);
await simulator.EnsureImageAsync();
await simulator.EnsurePrewarmAsync();

Console.WriteLine("Allocating three sessions...");
var alpha = await simulator.AcquireSessionAsync("alpha");
var beta = await simulator.AcquireSessionAsync("beta");
var gamma = await simulator.AcquireSessionAsync("gamma");

Dump(alpha);
Dump(beta);
Dump(gamma);

Console.WriteLine("Requesting delta (should be queued because max reached)...");
var delta = await simulator.AcquireSessionAsync("delta");
Dump(delta);

Console.WriteLine("Releasing session beta to free capacity...");
await simulator.ReleaseSessionAsync(beta.SessionId!);
var promoted = await simulator.TryPromoteQueueAsync();

Console.WriteLine("Queue after releasing beta:");
if (promoted is not null)
{
    Dump(promoted);
}
else
{
    Dump(delta);
}

Console.WriteLine("Releasing remaining sessions...");
await simulator.ReleaseSessionAsync(alpha.SessionId!);
await simulator.ReleaseSessionAsync(gamma.SessionId!);
if (promoted is not null)
{
    await simulator.ReleaseSessionAsync(promoted.SessionId!);
}

Console.WriteLine("Ensuring bulk cleanup...");
await simulator.ShutdownAsync();
Console.WriteLine("Simulation complete.");

static void Dump(SessionLease lease)
{
    var status = lease.IsQueued ? $"Queued (pos {lease.QueuePosition})" : $"Active container {lease.ContainerId}";
    Console.WriteLine($" - Session {lease.RequestedBy} -> {status}");
}

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

sealed class DockerPoolSimulator
{
    private readonly DockerClient _client;
    private readonly PoolOptions _options;
    private readonly Dictionary<string, ManagedContainer> _containers = new();
    private readonly Dictionary<string, SessionLease> _sessions = new();
    private readonly Queue<PendingRequest> _queue = new();

    public DockerPoolSimulator(DockerClient client, PoolOptions options)
    {
        _client = client;
        _options = options;
    }

    public async Task EnsureImageAsync()
    {
        try
        {
            await _client.Images.InspectImageAsync(_options.Image);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine($"Pulling image {_options.Image}...");
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = _options.Image },
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

    public async Task EnsurePrewarmAsync()
    {
        while (IdleCount < _options.Prewarm && _containers.Count < _options.MaxContainers)
        {
            var container = await CreateContainerAsync();
            _containers[container.ContainerId] = container;
            Console.WriteLine($"Pre-warmed container {container.ShortId}.");
        }
    }

    public async Task<SessionLease> AcquireSessionAsync(string requestedBy)
    {
        if (_containers.Values.FirstOrDefault(c => c.IsIdle) is { } idle)
        {
            idle.IsIdle = false;
            var lease = CreateLease(requestedBy, idle);
            _sessions[lease.SessionId!] = lease;
            return lease;
        }

        if (_containers.Count < _options.MaxContainers)
        {
            var container = await CreateContainerAsync();
            _containers[container.ContainerId] = container;
            container.IsIdle = false;
            var lease = CreateLease(requestedBy, container);
            _sessions[lease.SessionId!] = lease;
            return lease;
        }

        var pending = new PendingRequest(requestedBy);
        _queue.Enqueue(pending);
        return new SessionLease
        {
            RequestedBy = requestedBy,
            IsQueued = true,
            QueuePosition = _queue.Count
        };
    }

    public async Task<SessionLease?> TryPromoteQueueAsync()
    {
        if (_queue.Count == 0)
        {
            return null;
        }

        if (_containers.Count >= _options.MaxContainers)
        {
            return null;
        }

        var next = _queue.Dequeue();
        var container = await CreateContainerAsync();
        _containers[container.ContainerId] = container;
        container.IsIdle = false;
        var lease = CreateLease(next.RequestedBy, container);
        _sessions[lease.SessionId!] = lease;
        Console.WriteLine($"Promoted queued request ({next.RequestedBy}) into {container.ShortId}.");
        return lease;
    }

    public async Task ReleaseSessionAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var lease))
        {
            return;
        }

        _sessions.Remove(sessionId);
        if (lease.ContainerId is not null && _containers.TryGetValue(lease.ContainerId, out var container))
        {
            await DestroyContainerAsync(container.ContainerId);
            _containers.Remove(container.ContainerId);
            Console.WriteLine($"Destroyed container {container.ShortId} for session {sessionId}.");
        }
    }

    public async Task ShutdownAsync()
    {
        foreach (var sessionId in _sessions.Keys.ToList())
        {
            await ReleaseSessionAsync(sessionId);
        }

        foreach (var container in _containers.Values.ToList())
        {
            await DestroyContainerAsync(container.ContainerId);
            _containers.Remove(container.ContainerId);
        }
    }

    private int IdleCount => _containers.Values.Count(c => c.IsIdle);

    private async Task<ManagedContainer> CreateContainerAsync()
    {
        var name = $"exp-pool-{Guid.NewGuid():N}";
        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _options.Image,
            Name = name,
            Cmd = new[] { "/bin/bash", "-lc", "tail -f /dev/null" },
            Labels = new Dictionary<string, string>
            {
                ["exp.module"] = "DockerPoolSimulation"
            }
        });

        await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
        return new ManagedContainer(response.ID, name) { IsIdle = true };
    }

    private static SessionLease CreateLease(string requestedBy, ManagedContainer container)
    {
        return new SessionLease
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ContainerId = container.ContainerId,
            RequestedBy = requestedBy,
            IsQueued = false
        };
    }

    private async Task DestroyContainerAsync(string containerId)
    {
        try
        {
            await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 2 });
        }
        catch (DockerApiException ex)
        {
            Console.WriteLine($"Stop failed: {ex.StatusCode}");
        }

        await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
    }
}

sealed record PoolOptions
{
    public required string Image { get; init; }
    public int Prewarm { get; init; } = 1;
    public int MaxContainers { get; init; } = 3;
}

sealed class ManagedContainer
{
    public ManagedContainer(string id, string name)
    {
        ContainerId = id;
        Name = name;
    }

    public string ContainerId { get; }
    public string Name { get; }
    public bool IsIdle { get; set; }
    public string ShortId => ContainerId[..12];
}

sealed class SessionLease
{
    public string? SessionId { get; set; }
    public string? ContainerId { get; set; }
    public required string RequestedBy { get; set; }
    public bool IsQueued { get; set; }
    public int QueuePosition { get; set; }
}

sealed class PendingRequest
{
    public PendingRequest(string requestedBy)
    {
        RequestedBy = requestedBy;
    }

    public string RequestedBy { get; }
}
