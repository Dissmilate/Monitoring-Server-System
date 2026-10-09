namespace ServerMonitor.Agent.Transport;

// Doubling delays with +-20% jitter.
internal sealed class ExponentialBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private TimeSpan _next;

    public ExponentialBackoff(TimeSpan initial, TimeSpan max)
    {
        _initial = initial;
        _max = max;
        _next = initial;
    }

    public TimeSpan Next()
    {
        var jittered = _next * (0.8 + (Random.Shared.NextDouble() * 0.4));
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, _max.Ticks));
        return jittered;
    }

    public void Reset() => _next = _initial;
}
