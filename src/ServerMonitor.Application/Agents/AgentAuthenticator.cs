using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Security;

namespace ServerMonitor.Application.Agents;

public sealed record AgentIdentity(Guid ServerId, Guid KeyId);

public sealed class AgentAuthenticator(IServerRepository servers)
{
    public async Task<AgentIdentity?> AuthenticateAsync(string apiKey, CancellationToken ct)
    {
        if (!ApiKeys.TryGetPrefix(apiKey, out var prefix))
        {
            return null;
        }

        var key = await servers.FindAgentKeyAsync(prefix, ct);
        if (key is null || !key.IsActive || !ApiKeys.Matches(apiKey, key.KeyHash))
        {
            return null;
        }

        return new AgentIdentity(key.ServerId, key.Id);
    }
}
