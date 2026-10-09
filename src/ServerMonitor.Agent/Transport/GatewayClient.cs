using Grpc.Core;
using Grpc.Net.Client;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Transport;

internal sealed record EnrollmentResult(Guid ServerId, string ServerName, string ApiKey);

internal interface IGatewayClient
{
    Task<EnrollmentResult> EnrollAsync(string enrollmentToken, HostInfo host, CancellationToken ct);

    Task<Guid> PingAsync(string apiKey, CancellationToken ct);

    Task<int> PushMetricsAsync(string apiKey, MetricBatch batch, CancellationToken ct);

    Task<int> PushLogsAsync(string apiKey, LogBatch batch, CancellationToken ct);

    AsyncDuplexStreamingCall<AgentMessage, ServerMessage> Connect(string apiKey, CancellationToken ct);
}

internal sealed class GrpcGatewayClient(GrpcChannel channel) : IGatewayClient, IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly AgentGateway.AgentGatewayClient _client = new(channel);

    public async Task<EnrollmentResult> EnrollAsync(string enrollmentToken, HostInfo host, CancellationToken ct)
    {
        var response = await _client.EnrollAsync(
            new EnrollRequest { EnrollmentToken = enrollmentToken, Host = host },
            deadline: Deadline(),
            cancellationToken: ct);

        return new EnrollmentResult(Guid.Parse(response.ServerId), response.ServerName, response.ApiKey);
    }

    public async Task<Guid> PingAsync(string apiKey, CancellationToken ct)
    {
        var response = await _client.PingAsync(
            new PingRequest(),
            headers: AuthHeaders(apiKey),
            deadline: Deadline(),
            cancellationToken: ct);

        return Guid.Parse(response.ServerId);
    }

    public async Task<int> PushMetricsAsync(string apiKey, MetricBatch batch, CancellationToken ct)
    {
        var ack = await _client.PushMetricsAsync(
            batch,
            headers: AuthHeaders(apiKey),
            deadline: Deadline(),
            cancellationToken: ct);

        return ack.Accepted;
    }

    public async Task<int> PushLogsAsync(string apiKey, LogBatch batch, CancellationToken ct)
    {
        var ack = await _client.PushLogsAsync(
            batch,
            headers: AuthHeaders(apiKey),
            deadline: Deadline(),
            cancellationToken: ct);

        return ack.Accepted;
    }

    public AsyncDuplexStreamingCall<AgentMessage, ServerMessage> Connect(string apiKey, CancellationToken ct) =>
        _client.Connect(headers: AuthHeaders(apiKey), cancellationToken: ct);

    public void Dispose() => channel.Dispose();

    // Keep-alive pings detect half-open connections (e.g. NAT dropped the mapping).
    public static GrpcChannel CreateChannel(string address) =>
        GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                EnableMultipleHttp2Connections = true,
            },
        });

    private static Metadata AuthHeaders(string apiKey) => new() { { "authorization", $"ApiKey {apiKey}" } };

    private static DateTime Deadline() => DateTime.UtcNow + CallTimeout;
}
