using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Domain.Tests.Servers;

public sealed class ServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    [Fact]
    public void Create_trims_name_and_starts_as_pending()
    {
        var server = Server.Create("  web-01  ", Now).Value;

        server.Name.ShouldBe("web-01");
        server.Status.ShouldBe(ServerStatus.Pending);
        server.CreatedAt.ShouldBe(Now);
        server.Id.Version.ShouldBe(7);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name(string name) =>
        Server.Create(name, Now).Error.ShouldBe(ServerErrors.NameRequired);

    [Fact]
    public void Create_rejects_too_long_name() =>
        Server.Create(new string('a', Server.MaxNameLength + 1), Now).Error.ShouldBe(ServerErrors.NameTooLong);

    [Fact]
    public void Rename_validates_and_trims()
    {
        var server = Server.Create("web-01", Now).Value;

        server.Rename(" ").Error.ShouldBe(ServerErrors.NameRequired);
        server.Name.ShouldBe("web-01");

        server.Rename(" web-02 ").IsSuccess.ShouldBeTrue();
        server.Name.ShouldBe("web-02");
    }

    [Fact]
    public void Issued_token_is_active_until_it_expires()
    {
        var server = Server.Create("web-01", Now).Value;

        var token = server.IssueEnrollmentToken("hash-1", Now, Lifetime);

        token.ServerId.ShouldBe(server.Id);
        token.ExpiresAt.ShouldBe(Now + Lifetime);
        token.IsActive(Now + Lifetime - TimeSpan.FromSeconds(1)).ShouldBeTrue();
        token.IsActive(Now + Lifetime).ShouldBeFalse();
    }

    [Fact]
    public void Issuing_a_token_revokes_the_previous_active_one()
    {
        var server = Server.Create("web-01", Now).Value;
        var first = server.IssueEnrollmentToken("hash-1", Now, Lifetime);

        var later = Now.AddMinutes(5);
        var second = server.IssueEnrollmentToken("hash-2", later, Lifetime);

        first.RevokedAt.ShouldBe(later);
        first.IsActive(later).ShouldBeFalse();
        second.IsActive(later).ShouldBeTrue();
        server.EnrollmentTokens.Count.ShouldBe(2);
    }
}
