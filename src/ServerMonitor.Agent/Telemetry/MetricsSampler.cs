using ServerMonitor.Agent.Metrics;

namespace ServerMonitor.Agent.Telemetry;

internal sealed class MetricsSampler(IMetricsSource source, ITelemetryBuffer buffer) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string SourceDescription => source.Description;

    public async Task PrimeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            source.Collect();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SampleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await buffer.AppendAsync(source.Collect(), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
