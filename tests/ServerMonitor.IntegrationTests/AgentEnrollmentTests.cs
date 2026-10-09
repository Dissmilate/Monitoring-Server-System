using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServerMonitor.Agent;
using ServerMonitor.Agent.Credentials;
using ServerMonitor.Agent.Enrollment;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Application.Security;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.IntegrationTests.Infrastructure;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.IntegrationTests;

public sealed class AgentEnrollmentTests(IntegrationFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agent_enrolls_with_a_token_and_the_dashboard_shows_its_host()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, token) = await AgentScenario.CreateServerWithTokenAsync(admin, Ct);
        using var gateway = Gateway();

        var enrollment = await gateway.EnrollAsync(token, AgentScenario.TestHost, Ct);

        enrollment.ServerId.ShouldBe(server.Id);
        enrollment.ServerName.ShouldBe(server.Name);
        (await gateway.PingAsync(enrollment.ApiKey, Ct)).ShouldBe(server.Id);

        var updated = await admin.GetFromJsonAsync<ServerDto>($"/api/servers/{server.Id}", Ct);
        updated!.Hostname.ShouldBe(AgentScenario.TestHost.Hostname);
        updated.OperatingSystem.ShouldBe(AgentScenario.TestHost.OsDescription);
        updated.AgentVersion.ShouldBe(AgentScenario.TestHost.AgentVersion);
        updated.Status.ShouldBe("Offline");
        updated.AgentKeyPrefix.ShouldBe(enrollment.ApiKey[..ApiKeys.PrefixLength]);
    }

    [Fact]
    public async Task Enrollment_token_is_single_use()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (_, token) = await AgentScenario.CreateServerWithTokenAsync(admin, Ct);
        using var gateway = Gateway();
        await gateway.EnrollAsync(token, AgentScenario.TestHost, Ct);

        var error = await Should.ThrowAsync<RpcException>(() => gateway.EnrollAsync(token, AgentScenario.TestHost, Ct));

        error.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Concurrent_enrollments_with_one_token_produce_exactly_one_agent()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (_, token) = await AgentScenario.CreateServerWithTokenAsync(admin, Ct);
        using var gateway = Gateway();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                await gateway.EnrollAsync(token, AgentScenario.TestHost, Ct);
                return true;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                return false;
            }
        }));

        attempts.Count(succeeded => succeeded).ShouldBe(1);
    }

    [Theory]
    [InlineData("sme_unknown-token")]
    [InlineData("")]
    public async Task Unknown_token_is_denied(string token)
    {
        using var gateway = Gateway();

        var error = await Should.ThrowAsync<RpcException>(() => gateway.EnrollAsync(token, AgentScenario.TestHost, Ct));

        error.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("smk_0123456789ab_not-a-real-secret")]
    public async Task Calls_without_a_valid_key_are_unauthenticated(string? apiKey)
    {
        var client = new AgentGateway.AgentGatewayClient(fixture.CreateGatewayChannel());
        var headers = apiKey is null ? null : new Metadata { { "authorization", $"ApiKey {apiKey}" } };

        var error = await Should.ThrowAsync<RpcException>(
            async () => await client.PingAsync(new PingRequest(), headers, cancellationToken: Ct));

        error.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Revoking_agent_keys_locks_the_agent_out()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, token) = await AgentScenario.CreateServerWithTokenAsync(admin, Ct);
        using var gateway = Gateway();
        var apiKey = (await gateway.EnrollAsync(token, AgentScenario.TestHost, Ct)).ApiKey;

        using (var revoke = await admin.DeleteAsync(new Uri($"/api/servers/{server.Id}/agent-keys", UriKind.Relative), Ct))
        {
            revoke.EnsureSuccessStatusCode();
        }

        var error = await Should.ThrowAsync<RpcException>(() => gateway.PingAsync(apiKey, Ct));
        error.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        (await admin.GetFromJsonAsync<ServerDto>($"/api/servers/{server.Id}", Ct))!.AgentKeyPrefix.ShouldBeNull();
    }

    [Fact]
    public async Task Re_enrollment_replaces_the_previous_key()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, firstToken) = await AgentScenario.CreateServerWithTokenAsync(admin, Ct);
        using var gateway = Gateway();
        var oldKey = (await gateway.EnrollAsync(firstToken, AgentScenario.TestHost, Ct)).ApiKey;

        var newKey = (await gateway.EnrollAsync(await AgentScenario.IssueTokenAsync(admin, server.Id, Ct), AgentScenario.TestHost, Ct)).ApiKey;

        (await gateway.PingAsync(newKey, Ct)).ShouldBe(server.Id);
        var error = await Should.ThrowAsync<RpcException>(() => gateway.PingAsync(oldKey, Ct));
        error.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Real_agent_enrolls_once_and_then_starts_from_stored_credentials()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, token) = await AgentScenario.CreateServerWithTokenAsync(admin, Ct);
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"sm-agent-it-{Guid.NewGuid():N}");
        using var gateway = Gateway();
        var store = new FileCredentialStore(dataDirectory, SecretProtectors.ForCurrentPlatform());
        var options = Options.Create(new AgentOptions { GatewayUrl = "http://gateway", EnrollmentToken = token });

        try
        {
            var first = await new EnrollmentService(gateway, store, options, NullLogger<EnrollmentService>.Instance)
                .EnsureEnrolledAsync(Ct);

            var second = await new EnrollmentService(gateway, store, options, NullLogger<EnrollmentService>.Instance)
                .EnsureEnrolledAsync(Ct);

            first.ServerId.ShouldBe(server.Id);
            second.ShouldBe(first);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private GrpcGatewayClient Gateway() => new(fixture.CreateGatewayChannel());
}
