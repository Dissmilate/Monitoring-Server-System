using System.Text.Json;
using System.Text.Json.Serialization;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Control;

internal sealed record RuntimeConfig(long Version, TimeSpan MetricsInterval, TimeSpan HeartbeatInterval);

internal sealed record RuntimeConfigFile(long Version, int MetricsIntervalSeconds, int HeartbeatIntervalSeconds);

[JsonSerializable(typeof(RuntimeConfigFile))]
internal sealed partial class RuntimeConfigJsonContext : JsonSerializerContext;

internal sealed partial class RuntimeConfiguration
{
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly string _path;
    private readonly ILogger<RuntimeConfiguration> _logger;
    private readonly Lock _gate = new();
    private RuntimeConfig _current;

    public RuntimeConfiguration(string dataDirectory, TimeSpan defaultMetricsInterval, ILogger<RuntimeConfiguration> logger)
    {
        _path = Path.Combine(dataDirectory, "runtime-config.json");
        _logger = logger;
        _current = Load() ?? new RuntimeConfig(0, defaultMetricsInterval, DefaultHeartbeatInterval);
    }

    public event Action<RuntimeConfig>? Changed;

    public RuntimeConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public string? Apply(AgentConfig config)
    {
        if (config.MetricsIntervalSeconds is < 1 or > 3600)
        {
            return $"Metrics interval {config.MetricsIntervalSeconds}s is outside 1..3600 s.";
        }

        if (config.HeartbeatIntervalSeconds is < 1 or > 300)
        {
            return $"Heartbeat interval {config.HeartbeatIntervalSeconds}s is outside 1..300 s.";
        }

        var next = new RuntimeConfig(
            config.Version,
            TimeSpan.FromSeconds(config.MetricsIntervalSeconds),
            TimeSpan.FromSeconds(config.HeartbeatIntervalSeconds));

        lock (_gate)
        {
            if (next == _current)
            {
                return null;
            }

            _current = next;
            Save(next);
        }

        LogApplied(_logger, next.Version, next.MetricsInterval, next.HeartbeatInterval);
        Changed?.Invoke(next);
        return null;
    }

    private RuntimeConfig? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize(File.ReadAllBytes(_path), RuntimeConfigJsonContext.Default.RuntimeConfigFile);
            return file is null
                ? null
                : new RuntimeConfig(
                    file.Version,
                    TimeSpan.FromSeconds(file.MetricsIntervalSeconds),
                    TimeSpan.FromSeconds(file.HeartbeatIntervalSeconds));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogLoadFailed(_logger, ex, _path);
            return null;
        }
    }

    private void Save(RuntimeConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new RuntimeConfigFile(
                config.Version, (int)config.MetricsInterval.TotalSeconds, (int)config.HeartbeatInterval.TotalSeconds);

            var tempPath = _path + ".tmp";
            File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(file, RuntimeConfigJsonContext.Default.RuntimeConfigFile));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex, _path);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied configuration v{Version}: metrics every {MetricsInterval}, heartbeat every {HeartbeatInterval}")]
    private static partial void LogApplied(ILogger logger, long version, TimeSpan metricsInterval, TimeSpan heartbeatInterval);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read {Path}; using defaults until the server pushes a configuration")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not persist the configuration to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}
