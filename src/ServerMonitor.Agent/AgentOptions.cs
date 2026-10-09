using ServerMonitor.Agent.Logs;

namespace ServerMonitor.Agent;

internal sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string GatewayUrl { get; set; } = string.Empty;

    public string? EnrollmentToken { get; set; }

    public string? DataDirectory { get; set; }

    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(10);

    public bool? UseCgroupMetrics { get; set; }

    public int BufferCapacity { get; set; } = 50_000;

    public LogOptions Logs { get; set; } = new();

    public string ResolveDataDirectory() =>
        string.IsNullOrWhiteSpace(DataDirectory) ? DefaultDataDirectory() : DataDirectory;

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServerMonitor", "Agent");
        }

        return Environment.IsPrivilegedProcess
            ? "/var/lib/server-monitor-agent"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "server-monitor-agent");
    }
}
