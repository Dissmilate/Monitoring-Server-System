using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Application.Security;
using ServerMonitor.Application.Servers;
using ServerMonitor.Application.Tests.Fakes;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Tests.Servers;

public sealed class ServerHandlerTests
{
    private readonly InMemoryServerRepository _repository = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_persists_a_pending_server()
    {
        var result = await CreateHandler().HandleAsync(new CreateServerRequest("web-01"), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(nameof(ServerStatus.Pending));
        result.Value.CreatedAt.ShouldBe(_time.GetUtcNow());
        _repository.Servers.ShouldHaveSingleItem().Id.ShouldBe(result.Value.Id);
        _repository.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_name_without_saving()
    {
        await CreateHandler().HandleAsync(new CreateServerRequest("web-01"), Ct);

        var result = await CreateHandler().HandleAsync(new CreateServerRequest(" web-01 "), Ct);

        result.Error!.Type.ShouldBe(ErrorType.Conflict);
        _repository.Servers.Count.ShouldBe(1);
        _repository.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Rename_to_another_servers_name_is_a_conflict()
    {
        await CreateHandler().HandleAsync(new CreateServerRequest("web-01"), Ct);
        var second = await CreateHandler().HandleAsync(new CreateServerRequest("web-02"), Ct);

        var result = await new RenameServerHandler(_repository, _repository)
            .HandleAsync(second.Value.Id, new RenameServerRequest("web-01"), Ct);

        result.Error!.Code.ShouldBe("Server.NameTaken");
    }

    [Fact]
    public async Task Issued_token_is_returned_once_and_stored_as_hash()
    {
        var server = (await CreateHandler().HandleAsync(new CreateServerRequest("web-01"), Ct)).Value;
        var lifetime = TimeSpan.FromHours(2);

        var result = await IssueTokenHandler(lifetime).HandleAsync(server.Id, Ct);

        var token = result.Value;
        token.Token.ShouldStartWith(SecretTokens.EnrollmentPrefix + "_");
        token.ExpiresAt.ShouldBe(_time.GetUtcNow() + lifetime);

        var stored = _repository.Servers.Single().EnrollmentTokens.ShouldHaveSingleItem();
        stored.TokenHash.ShouldBe(SecretTokens.Hash(token.Token));
        stored.TokenHash.ShouldNotContain(token.Token);
    }

    [Fact]
    public async Task Issuing_a_token_for_an_unknown_server_is_not_found()
    {
        var result = await IssueTokenHandler(TimeSpan.FromHours(1)).HandleAsync(Guid.NewGuid(), Ct);

        result.Error!.Type.ShouldBe(ErrorType.NotFound);
    }

    private CreateServerHandler CreateHandler() => new(_repository, _repository, _time);

    private IssueEnrollmentTokenHandler IssueTokenHandler(TimeSpan lifetime) =>
        new(_repository, _repository, _time, Options.Create(new EnrollmentOptions { TokenLifetime = lifetime }));
}
