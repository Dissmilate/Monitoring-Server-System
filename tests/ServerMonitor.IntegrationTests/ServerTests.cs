using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Application.Security;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Contracts.Users;
using ServerMonitor.Domain.Servers;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.IntegrationTests.Infrastructure;

namespace ServerMonitor.IntegrationTests;

public sealed class ServerTests(IntegrationFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/servers", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_creates_a_server_that_appears_in_the_list()
    {
        using var client = await fixture.CreateAdminClientAsync(Ct);
        var name = HttpClientExtensions.UniqueName("web");

        using var response = await client.PostAsJsonAsync("/api/servers", new CreateServerRequest(name), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<ServerDto>(Ct))!;
        created.Status.ShouldBe(nameof(ServerStatus.Pending));
        response.Headers.Location!.ToString().ShouldBe($"/api/servers/{created.Id}");

        var list = await client.GetFromJsonAsync<List<ServerDto>>("/api/servers", Ct);
        list!.ShouldContain(s => s.Id == created.Id && s.Name == name);
    }

    [Fact]
    public async Task Duplicate_name_is_a_conflict()
    {
        using var client = await fixture.CreateAdminClientAsync(Ct);
        var name = HttpClientExtensions.UniqueName("db");
        (await client.PostAsJsonAsync("/api/servers", new CreateServerRequest(name), Ct)).Dispose();

        using var response = await client.PostAsJsonAsync("/api/servers", new CreateServerRequest(name), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("Server.NameTaken");
    }

    [Fact]
    public async Task Blank_name_is_a_validation_problem()
    {
        using var client = await fixture.CreateAdminClientAsync(Ct);

        using var response = await client.PostAsJsonAsync("/api/servers", new CreateServerRequest("  "), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe(ServerErrors.NameRequired.Code);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_modify()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var email = $"{HttpClientExtensions.UniqueName("viewer")}@test.local";
        const string password = "Viewer-Password-1";
        using (var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, password, Roles.Viewer), Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var viewer = fixture.CreateClient();
        await viewer.LoginAsync(email, password, Ct);

        using var list = await viewer.GetAsync(new Uri("/api/servers", UriKind.Relative), Ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var create = await viewer.PostAsJsonAsync("/api/servers", new CreateServerRequest("forbidden"), Ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var users = await viewer.GetAsync(new Uri("/api/users", UriKind.Relative), Ct);
        users.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enrollment_token_is_stored_only_as_a_hash_and_reissue_revokes_the_previous_one()
    {
        using var client = await fixture.CreateAdminClientAsync(Ct);
        var server = await CreateServerAsync(client);

        var first = await IssueTokenAsync(client, server.Id);
        var second = await IssueTokenAsync(client, server.Id);

        first.Token.ShouldStartWith(SecretTokens.EnrollmentPrefix + "_");
        first.Token.ShouldNotBe(second.Token);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
        var stored = await db.Set<EnrollmentToken>()
            .Where(t => t.ServerId == server.Id)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(Ct);

        stored.Select(t => t.TokenHash).ShouldBe([SecretTokens.Hash(first.Token), SecretTokens.Hash(second.Token)]);
        stored[0].RevokedAt.ShouldNotBeNull();
        stored[1].RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Deleted_server_is_gone()
    {
        using var client = await fixture.CreateAdminClientAsync(Ct);
        var server = await CreateServerAsync(client);
        await IssueTokenAsync(client, server.Id);

        using var delete = await client.DeleteAsync(new Uri($"/api/servers/{server.Id}", UriKind.Relative), Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var get = await client.GetAsync(new Uri($"/api/servers/{server.Id}", UriKind.Relative), Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<ServerDto> CreateServerAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/servers", new CreateServerRequest(HttpClientExtensions.UniqueName("srv")), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServerDto>(Ct))!;
    }

    private async Task<EnrollmentTokenDto> IssueTokenAsync(HttpClient client, Guid serverId)
    {
        using var response = await client.PostAsync(
            new Uri($"/api/servers/{serverId}/enrollment-tokens", UriKind.Relative), content: null, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentTokenDto>(Ct))!;
    }
}
