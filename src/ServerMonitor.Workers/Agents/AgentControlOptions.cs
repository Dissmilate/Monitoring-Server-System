namespace ServerMonitor.Workers.Agents;

public sealed class PresenceOptions
{
    public const string SectionName = "Presence";

    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(45);

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class CommandRelayOptions
{
    public const string SectionName = "CommandRelay";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; set; } = 100;
}
