namespace ServerMonitor.Gateway.Agents;

public sealed class AgentChannelOptions
{
    public const string SectionName = "AgentChannel";

    // Must be well below Presence:OfflineAfter in Workers.
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ResyncInterval { get; set; } = TimeSpan.FromMinutes(1);
}
