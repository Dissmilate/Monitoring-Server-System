using System.Text.Json.Serialization;

namespace ServerMonitor.Agent.Credentials;

internal sealed record AgentCredentials(Guid ServerId, string ApiKey);

internal interface ICredentialStore
{
    Task<AgentCredentials?> LoadAsync(CancellationToken ct);

    Task SaveAsync(AgentCredentials credentials, CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);
}

[JsonSerializable(typeof(AgentCredentials))]
internal sealed partial class CredentialsJsonContext : JsonSerializerContext;
