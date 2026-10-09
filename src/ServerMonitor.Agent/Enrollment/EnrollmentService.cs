using System.Reflection;
using System.Runtime.InteropServices;
using Grpc.Core;
using Microsoft.Extensions.Options;
using ServerMonitor.Agent.Credentials;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Enrollment;

internal sealed class AgentEnrollmentException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed partial class EnrollmentService(
    IGatewayClient gateway,
    ICredentialStore store,
    IOptions<AgentOptions> options,
    ILogger<EnrollmentService> logger)
{
    public async Task<AgentCredentials> EnsureEnrolledAsync(CancellationToken ct)
    {
        var stored = await store.LoadAsync(ct);
        if (stored is not null)
        {
            if (await IsAcceptedAsync(stored, ct))
            {
                LogUsingStoredCredentials(logger, stored.ServerId);
                return stored;
            }

            LogStoredKeyRejected(logger, stored.ServerId);
            await store.DeleteAsync(ct);
        }

        var token = options.Value.EnrollmentToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new AgentEnrollmentException(
                "The agent is not enrolled. Issue an enrollment token in the dashboard and set Agent:EnrollmentToken.");
        }

        var credentials = await EnrollAsync(token.Trim(), ct);
        await store.SaveAsync(credentials, ct);

        if (!await IsAcceptedAsync(credentials, ct))
        {
            throw new AgentEnrollmentException("The Gateway rejected the API key it has just issued.");
        }

        return credentials;
    }

    internal static HostInfo DescribeHost() => new()
    {
        Hostname = Environment.MachineName,
        OsDescription = RuntimeInformation.OSDescription,
        AgentVersion = AgentVersion(),
    };

    private async Task<AgentCredentials> EnrollAsync(string token, CancellationToken ct)
    {
        try
        {
            var result = await gateway.EnrollAsync(token, DescribeHost(), ct);
            LogEnrolled(logger, result.ServerId, result.ServerName);
            return new AgentCredentials(result.ServerId, result.ApiKey);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            throw new AgentEnrollmentException(
                $"Enrollment token was rejected: {ex.Status.Detail} Issue a new token in the dashboard.", ex);
        }
    }

    private async Task<bool> IsAcceptedAsync(AgentCredentials credentials, CancellationToken ct)
    {
        try
        {
            return await gateway.PingAsync(credentials.ApiKey, ct) == credentials.ServerId;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            return false;
        }
    }

    private static string AgentVersion()
    {
        var version = typeof(EnrollmentService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled as server {ServerId} ({ServerName})")]
    private static partial void LogEnrolled(ILogger logger, Guid serverId, string serverName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Using stored credentials for server {ServerId}")]
    private static partial void LogUsingStoredCredentials(ILogger logger, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored API key for server {ServerId} was rejected; trying to re-enroll")]
    private static partial void LogStoredKeyRejected(ILogger logger, Guid serverId);
}
