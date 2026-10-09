using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Application.Agents;
using ServerMonitor.Application.Security;
using ServerMonitor.Application.Tests.Fakes;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Tests.Agents;

public sealed class AgentHandlerTests
{
    private static readonly AgentHost Host = new("web-01.local", "Ubuntu 24.04", "0.2.0");

    private readonly InMemoryServerRepository _repository = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enrollment_returns_a_key_that_authenticates_as_the_server()
    {
        var (server, token) = ServerWithToken();

        var enrollment = (await EnrollHandler().HandleAsync(new EnrollAgentCommand(token, Host), Ct)).Value;

        enrollment.ServerId.ShouldBe(server.Id);
        server.ActiveAgentKey!.Prefix.ShouldBe(enrollment.ApiKey[..ApiKeys.PrefixLength]);
        server.ActiveAgentKey.KeyHash.ShouldNotContain(enrollment.ApiKey);

        var identity = await new AgentAuthenticator(_repository).AuthenticateAsync(enrollment.ApiKey, Ct);
        identity.ShouldBe(new AgentIdentity(server.Id, server.ActiveAgentKey.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sme_unknown")]
    public async Task Unknown_token_is_rejected(string token)
    {
        ServerWithToken();

        var result = await EnrollHandler().HandleAsync(new EnrollAgentCommand(token, Host), Ct);

        result.Error.ShouldBe(ServerErrors.InvalidEnrollmentToken);
        _repository.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Losing_a_concurrency_race_is_reported_as_an_invalid_token()
    {
        var (_, token) = ServerWithToken();
        _repository.FailNextSaveWithConcurrencyConflict = true;

        var result = await EnrollHandler().HandleAsync(new EnrollAgentCommand(token, Host), Ct);

        result.Error.ShouldBe(ServerErrors.InvalidEnrollmentToken);
    }

    [Fact]
    public async Task Revoked_and_tampered_keys_do_not_authenticate()
    {
        var (server, token) = ServerWithToken();
        var apiKey = (await EnrollHandler().HandleAsync(new EnrollAgentCommand(token, Host), Ct)).Value.ApiKey;
        var authenticator = new AgentAuthenticator(_repository);

        (await authenticator.AuthenticateAsync(apiKey[..^1] + (apiKey[^1] == 'A' ? 'B' : 'A'), Ct)).ShouldBeNull();
        (await authenticator.AuthenticateAsync("not-a-key", Ct)).ShouldBeNull();

        server.RevokeAgentKeys(_time.GetUtcNow());

        (await authenticator.AuthenticateAsync(apiKey, Ct)).ShouldBeNull();
    }

    private (Server Server, string Token) ServerWithToken()
    {
        var server = Server.Create("web-01", _time.GetUtcNow()).Value;
        var token = SecretTokens.Generate(SecretTokens.EnrollmentPrefix);
        server.IssueEnrollmentToken(SecretTokens.Hash(token), _time.GetUtcNow(), TimeSpan.FromHours(1));
        _repository.Add(server);
        return (server, token);
    }

    private EnrollAgentHandler EnrollHandler() => new(_repository, _repository, _time);
}
