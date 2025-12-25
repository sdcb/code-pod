using CodePod.Sdk.Models;

namespace CodePod.Sdk.Tests.TestInfrastructure;

/// <summary>
/// Test-only helper that tracks created sessions and ensures they are destroyed
/// when the test completes (via await using).
/// </summary>
public sealed class TestSessionTracker : IAsyncDisposable
{
    private readonly CodePodClient _client;
    private readonly List<int> _sessionIds = [];
    private bool _disposed;

    public TestSessionTracker(CodePodClient client)
    {
        _client = client;
    }

    public async Task<SessionInfo> CreateSessionAsync(
        string? name = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        SessionInfo session = await _client.CreateSessionAsync(name, timeoutSeconds, cancellationToken);
        _sessionIds.Add(session.Id);
        return session;
    }

    public async Task<SessionInfo> CreateSessionAsync(
        SessionOptions options,
        CancellationToken cancellationToken = default)
    {
        SessionInfo session = await _client.CreateSessionAsync(options, cancellationToken);
        _sessionIds.Add(session.Id);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Destroy in reverse order to better mimic stack-like ownership.
        for (int i = _sessionIds.Count - 1; i >= 0; i--)
        {
            try
            {
                await _client.DestroySessionAsync(_sessionIds[i]);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }

        _sessionIds.Clear();
    }
}
