using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using ServerMonitor.Contracts.Live;

namespace ServerMonitor.Dashboard.Services;

// Hub groups are lost on reconnect, so watched servers are re-registered.
public sealed class LiveUpdates(NavigationManager navigation) : IAsyncDisposable
{
    private readonly HashSet<Guid> _watched = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private HubConnection? _connection;

    public event Action<ServerStatusChangedDto>? StatusChanged;

    public event Action<ServerSummaryDto>? SummaryUpdated;

    public event Action<LiveSampleDto>? SampleReceived;

    public event Action<HubConnectionState>? ConnectionChanged;

    // Raised after an automatic reconnect: anything sent while offline was missed, so pages reload.
    public event Action? Reconnected;

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public async Task EnsureStartedAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            _connection ??= Build();
            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync();
                ConnectionChanged?.Invoke(_connection.State);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task WatchAsync(Guid serverId)
    {
        await EnsureStartedAsync();
        _watched.Add(serverId);
        await _connection!.InvokeAsync(LiveHub.WatchServer, serverId);
    }

    public async Task UnwatchAsync(Guid serverId)
    {
        _watched.Remove(serverId);
        if (_connection is { State: HubConnectionState.Connected })
        {
            await _connection.InvokeAsync(LiveHub.UnwatchServer, serverId);
        }
    }

    public async Task StopAsync()
    {
        _watched.Clear();
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
            ConnectionChanged?.Invoke(HubConnectionState.Disconnected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _startGate.Dispose();
    }

    private HubConnection Build()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(LiveHub.Path))
            .WithAutomaticReconnect(new RetryForever())
            .Build();

        connection.On<ServerStatusChangedDto>(nameof(ILiveClient.ServerStatusChanged), dto => StatusChanged?.Invoke(dto));
        connection.On<ServerSummaryDto>(nameof(ILiveClient.ServerSummaryUpdated), dto => SummaryUpdated?.Invoke(dto));
        connection.On<LiveSampleDto>(nameof(ILiveClient.MetricsReceived), dto => SampleReceived?.Invoke(dto));

        connection.Reconnecting += _ =>
        {
            ConnectionChanged?.Invoke(HubConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            foreach (var serverId in _watched)
            {
                await connection.InvokeAsync(LiveHub.WatchServer, serverId);
            }

            ConnectionChanged?.Invoke(HubConnectionState.Connected);
            Reconnected?.Invoke();
        };
        connection.Closed += _ =>
        {
            ConnectionChanged?.Invoke(HubConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        return connection;
    }

    // The default policy gives up after ~40 s, so a laptop waking from sleep would stay offline.
    private sealed class RetryForever : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
            [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < Delays.Length
                ? Delays[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(15);
    }
}
