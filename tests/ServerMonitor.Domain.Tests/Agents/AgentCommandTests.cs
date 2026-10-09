using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Domain.Tests.Agents;

public sealed class AgentCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentCommandType.CollectNow, 5)]
    [InlineData(AgentCommandType.ApplyConfig, 24 * 60)]
    public void Lifetime_depends_on_the_command_type(AgentCommandType type, int minutes) =>
        AgentCommand.Create(Guid.NewGuid(), type, Now).ExpiresAt.ShouldBe(Now.AddMinutes(minutes));

    [Fact]
    public void Delivered_commands_stay_deliverable_until_a_result_arrives()
    {
        var command = AgentCommand.Create(Guid.NewGuid(), AgentCommandType.CollectNow, Now);

        command.MarkDelivered(Now.AddSeconds(1));

        command.Status.ShouldBe(AgentCommandStatus.Delivered);
        command.IsDeliverable(Now.AddSeconds(2)).ShouldBeTrue();

        command.Complete(succeeded: true, "ok", Now.AddSeconds(3)).IsSuccess.ShouldBeTrue();
        command.IsDeliverable(Now.AddSeconds(4)).ShouldBeFalse();
    }

    [Fact]
    public void A_command_is_completed_only_once_and_long_messages_are_truncated()
    {
        var command = AgentCommand.Create(Guid.NewGuid(), AgentCommandType.CollectNow, Now);

        command.Complete(succeeded: false, new string('x', 5000), Now).IsSuccess.ShouldBeTrue();
        var again = command.Complete(succeeded: true, "late duplicate", Now);

        again.Error.ShouldBe(AgentCommandErrors.AlreadyCompleted(command.Id));
        command.Status.ShouldBe(AgentCommandStatus.Failed);
        command.ResultMessage!.Length.ShouldBe(AgentCommand.MaxResultMessageLength);
    }

    [Fact]
    public void Commands_past_their_lifetime_are_not_deliverable() =>
        AgentCommand.Create(Guid.NewGuid(), AgentCommandType.CollectNow, Now)
            .IsDeliverable(Now.AddMinutes(5)).ShouldBeFalse();
}

public sealed class ServerConfigurationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_servers_start_with_default_configuration_version_one()
    {
        var server = Server.Create("web-01", Now).Value;

        server.ConfigVersion.ShouldBe(1);
        server.MetricsIntervalSeconds.ShouldBe(Server.DefaultMetricsIntervalSeconds);
    }

    [Fact]
    public void Changing_the_configuration_bumps_its_version_only_on_real_changes()
    {
        var server = Server.Create("web-01", Now).Value;

        server.UpdateConfiguration(30).Value.ShouldBeTrue();
        server.UpdateConfiguration(30).Value.ShouldBeFalse();

        server.ConfigVersion.ShouldBe(2);
        server.MetricsIntervalSeconds.ShouldBe(30);
    }

    [Theory]
    [InlineData(Server.MinMetricsIntervalSeconds - 1)]
    [InlineData(Server.MaxMetricsIntervalSeconds + 1)]
    public void Intervals_outside_the_allowed_range_are_rejected(int seconds) =>
        Server.Create("web-01", Now).Value.UpdateConfiguration(seconds).Error.ShouldBe(ServerErrors.InvalidMetricsInterval);

    [Fact]
    public void Every_change_to_the_aggregate_increments_its_concurrency_version()
    {
        var server = Server.Create("web-01", Now).Value;

        server.Rename("web-02");
        server.IssueEnrollmentToken("hash", Now, TimeSpan.FromHours(1));
        server.UpdateConfiguration(60);

        server.Version.ShouldBe(3);
    }
}
