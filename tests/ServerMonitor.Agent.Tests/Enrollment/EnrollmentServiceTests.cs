using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServerMonitor.Agent.Credentials;
using ServerMonitor.Agent.Enrollment;
using ServerMonitor.Agent.Tests.Fakes;

namespace ServerMonitor.Agent.Tests.Enrollment;

public sealed class EnrollmentServiceTests
{
    private static readonly Guid ServerId = Guid.NewGuid();

    private readonly FakeGatewayClient _gateway = new();
    private readonly InMemoryCredentialStore _store = new();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_start_enrolls_with_the_token_and_saves_credentials()
    {
        _gateway.AddToken("sme_token", ServerId);

        var credentials = await Service(token: "sme_token").EnsureEnrolledAsync(Ct);

        credentials.ServerId.ShouldBe(ServerId);
        _store.Credentials.ShouldBe(credentials);
        _gateway.EnrollCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Stored_credentials_are_reused_without_enrolling()
    {
        _gateway.AddKey("smk_stored", ServerId);
        _store.Credentials = new AgentCredentials(ServerId, "smk_stored");

        var credentials = await Service(token: "sme_ignored").EnsureEnrolledAsync(Ct);

        credentials.ApiKey.ShouldBe("smk_stored");
        _gateway.EnrollCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Revoked_key_falls_back_to_a_new_enrollment_token()
    {
        _store.Credentials = new AgentCredentials(ServerId, "smk_revoked");
        _gateway.AddToken("sme_new", ServerId);

        var credentials = await Service(token: "sme_new").EnsureEnrolledAsync(Ct);

        credentials.ApiKey.ShouldNotBe("smk_revoked");
        _store.Credentials.ShouldBe(credentials);
    }

    [Fact]
    public async Task Without_credentials_or_token_the_agent_cannot_start()
    {
        var error = await Should.ThrowAsync<AgentEnrollmentException>(() => Service(token: null).EnsureEnrolledAsync(Ct));

        error.Message.ShouldContain("Agent:EnrollmentToken");
    }

    [Fact]
    public async Task Rejected_token_is_reported_and_nothing_is_stored()
    {
        _store.Credentials = new AgentCredentials(ServerId, "smk_revoked");

        await Should.ThrowAsync<AgentEnrollmentException>(() => Service(token: "sme_used").EnsureEnrolledAsync(Ct));

        _store.Credentials.ShouldBeNull();
    }

    [Fact]
    public void Host_description_has_no_build_metadata()
    {
        var host = EnrollmentService.DescribeHost();

        host.Hostname.ShouldBe(Environment.MachineName);
        host.OsDescription.ShouldNotBeNullOrWhiteSpace();
        host.AgentVersion.ShouldNotContain("+");
    }

    private EnrollmentService Service(string? token) => new(
        _gateway,
        _store,
        Options.Create(new AgentOptions { GatewayUrl = "http://gateway:5090", EnrollmentToken = token }),
        NullLogger<EnrollmentService>.Instance);
}
