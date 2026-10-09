using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Domain.Tests.Servers;

public sealed class EnrollmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly AgentHost Host = new("web-01.local", "Ubuntu 24.04", "0.2.0");

    [Fact]
    public void Enroll_consumes_the_token_binds_a_key_and_records_the_host()
    {
        var server = ServerWithToken("token-hash");

        var key = server.Enroll("token-hash", Host, "smk_prefix", "key-hash", Now).Value;

        server.EnrollmentTokens.Single().UsedAt.ShouldBe(Now);
        server.ActiveAgentKey.ShouldBe(key);
        key.Prefix.ShouldBe("smk_prefix");
        server.Hostname.ShouldBe(Host.Hostname);
        server.OperatingSystem.ShouldBe(Host.OperatingSystem);
        server.AgentVersion.ShouldBe(Host.AgentVersion);
        server.Status.ShouldBe(ServerStatus.Offline);
        server.LastSeenAt.ShouldBe(Now);
    }

    [Fact]
    public void A_token_cannot_be_used_twice()
    {
        var server = ServerWithToken("token-hash");
        server.Enroll("token-hash", Host, "smk_1", "hash-1", Now);

        var second = server.Enroll("token-hash", Host, "smk_2", "hash-2", Now.AddMinutes(1));

        second.Error.ShouldBe(ServerErrors.InvalidEnrollmentToken);
        server.AgentKeys.Count.ShouldBe(1);
    }

    [Fact]
    public void Expired_and_unknown_tokens_are_rejected()
    {
        var server = ServerWithToken("token-hash");

        server.Enroll("token-hash", Host, "smk_1", "hash-1", Now + Lifetime).Error
            .ShouldBe(ServerErrors.InvalidEnrollmentToken);
        server.Enroll("other-hash", Host, "smk_1", "hash-1", Now).Error
            .ShouldBe(ServerErrors.InvalidEnrollmentToken);
    }

    [Fact]
    public void Re_enrollment_revokes_the_previous_key()
    {
        var server = ServerWithToken("token-1");
        var first = server.Enroll("token-1", Host, "smk_1", "hash-1", Now).Value;
        server.IssueEnrollmentToken("token-2", Now, Lifetime);

        var second = server.Enroll("token-2", Host, "smk_2", "hash-2", Now.AddMinutes(1)).Value;

        first.IsActive.ShouldBeFalse();
        server.ActiveAgentKey.ShouldBe(second);
    }

    [Fact]
    public void Revoking_agent_keys_leaves_no_active_key()
    {
        var server = ServerWithToken("token-hash");
        server.Enroll("token-hash", Host, "smk_1", "hash-1", Now);

        server.RevokeAgentKeys(Now.AddHours(1));

        server.ActiveAgentKey.ShouldBeNull();
        server.AgentKeys.Single().RevokedAt.ShouldBe(Now.AddHours(1));
    }

    [Fact]
    public void Host_values_reported_by_the_agent_are_trimmed_and_capped()
    {
        var host = new AgentHost($"  {new string('h', 300)}  ", " Linux ", null!).Normalize();

        host.Hostname.Length.ShouldBe(AgentHost.MaxHostnameLength);
        host.OperatingSystem.ShouldBe("Linux");
        host.AgentVersion.ShouldBe(string.Empty);
    }

    private static Server ServerWithToken(string tokenHash)
    {
        var server = Server.Create("web-01", Now).Value;
        server.IssueEnrollmentToken(tokenHash, Now, Lifetime);
        return server;
    }
}
