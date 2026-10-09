using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ServerMonitor.Api.Auth;
using ServerMonitor.Contracts.Live;

namespace ServerMonitor.Api.Live;

[Authorize(Policy = Policies.Viewer)]
public sealed class LiveUpdatesHub : Hub<ILiveClient>
{
    public static string ServerGroup(Guid serverId) => $"server:{serverId}";

    [HubMethodName(LiveHub.WatchServer)]
    public Task WatchServer(Guid serverId) => Groups.AddToGroupAsync(Context.ConnectionId, ServerGroup(serverId));

    [HubMethodName(LiveHub.UnwatchServer)]
    public Task UnwatchServer(Guid serverId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, ServerGroup(serverId));
}
