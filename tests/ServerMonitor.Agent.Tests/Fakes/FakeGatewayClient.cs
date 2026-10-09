using Grpc.Core;
using ServerMonitor.Agent.Credentials;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Tests.Fakes;

internal sealed class FakeGatewayClient : IGatewayClient
{
    private readonly Dictionary<string, Guid> _tokens = [];
    private readonly Dictionary<string, Guid> _keys = [];

    public int EnrollCalls { get; private set; }

    public List<MetricBatch> PushedBatches { get; } = [];

    public StatusCode? PushFailure { get; set; }

    public void AddToken(string token, Guid serverId) => _tokens[token] = serverId;

    public void AddKey(string apiKey, Guid serverId) => _keys[apiKey] = serverId;

    public Task<EnrollmentResult> EnrollAsync(string enrollmentToken, HostInfo host, CancellationToken ct)
    {
        EnrollCalls++;
        if (!_tokens.Remove(enrollmentToken, out var serverId))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Enrollment token is invalid."));
        }

        var apiKey = $"smk_{Guid.NewGuid():N}";
        _keys[apiKey] = serverId;
        return Task.FromResult(new EnrollmentResult(serverId, "web-01", apiKey));
    }

    public Task<Guid> PingAsync(string apiKey, CancellationToken ct) =>
        _keys.TryGetValue(apiKey, out var serverId)
            ? Task.FromResult(serverId)
            : throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key."));

    public Task<int> PushMetricsAsync(string apiKey, MetricBatch batch, CancellationToken ct)
    {
        if (PushFailure is { } status)
        {
            throw new RpcException(new Status(status, "Simulated failure."));
        }

        PushedBatches.Add(batch);
        return Task.FromResult(batch.Samples.Count);
    }

    public List<LogBatch> PushedLogBatches { get; } = [];

    public StatusCode? PushLogsFailure { get; set; }

    public Task<int> PushLogsAsync(string apiKey, LogBatch batch, CancellationToken ct)
    {
        if (PushLogsFailure is { } status)
        {
            throw new RpcException(new Status(status, "Simulated failure."));
        }

        PushedLogBatches.Add(batch);
        return Task.FromResult(batch.Entries.Count);
    }

    public AsyncDuplexStreamingCall<AgentMessage, ServerMessage> Connect(string apiKey, CancellationToken ct) =>
        throw new NotSupportedException("The control channel is covered by the integration tests.");
}

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    public AgentCredentials? Credentials { get; set; }

    public Task<AgentCredentials?> LoadAsync(CancellationToken ct) => Task.FromResult(Credentials);

    public Task SaveAsync(AgentCredentials credentials, CancellationToken ct)
    {
        Credentials = credentials;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        Credentials = null;
        return Task.CompletedTask;
    }
}
