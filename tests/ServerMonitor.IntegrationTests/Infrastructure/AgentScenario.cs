using System.Net.Http.Json;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.IntegrationTests.Infrastructure;

internal static class AgentScenario
{
    public static readonly HostInfo TestHost = new()
    {
        Hostname = "test-host",
        OsDescription = "TestOS 1.0",
        AgentVersion = "0.0.0-test",
    };

    public static async Task<(ServerDto Server, string Token)> CreateServerWithTokenAsync(HttpClient admin, CancellationToken ct)
    {
        using var response = await admin.PostAsJsonAsync(
            "/api/servers", new CreateServerRequest(HttpClientExtensions.UniqueName("agent")), ct);
        response.EnsureSuccessStatusCode();
        var server = (await response.Content.ReadFromJsonAsync<ServerDto>(ct))!;

        return (server, await IssueTokenAsync(admin, server.Id, ct));
    }

    public static async Task<string> IssueTokenAsync(HttpClient admin, Guid serverId, CancellationToken ct)
    {
        using var response = await admin.PostAsync(
            new Uri($"/api/servers/{serverId}/enrollment-tokens", UriKind.Relative), content: null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentTokenDto>(ct))!.Token;
    }

    public static async Task<(ServerDto Server, string ApiKey)> EnrollAsync(
        IntegrationFixture fixture, HttpClient admin, CancellationToken ct)
    {
        var (server, token) = await CreateServerWithTokenAsync(admin, ct);
        using var gateway = new GrpcGatewayClient(fixture.CreateGatewayChannel());
        var enrollment = await gateway.EnrollAsync(token, TestHost, ct);
        return (server, enrollment.ApiKey);
    }
}
