namespace ServerMonitor.Domain.Servers;

public sealed record AgentHost(string Hostname, string OperatingSystem, string AgentVersion)
{
    public const int MaxHostnameLength = 255;
    public const int MaxOperatingSystemLength = 255;
    public const int MaxAgentVersionLength = 64;

    public AgentHost Normalize() => new(
        Cap(Hostname, MaxHostnameLength),
        Cap(OperatingSystem, MaxOperatingSystemLength),
        Cap(AgentVersion, MaxAgentVersionLength));

    private static string Cap(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
