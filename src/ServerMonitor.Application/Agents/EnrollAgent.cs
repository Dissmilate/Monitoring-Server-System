using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Security;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Agents;

public sealed record EnrollAgentCommand(string EnrollmentToken, AgentHost Host);

public sealed record AgentEnrollment(Guid ServerId, string ServerName, string ApiKey);

public sealed class EnrollAgentHandler(IServerRepository servers, IUnitOfWork unitOfWork, TimeProvider time)
{
    public async Task<Result<AgentEnrollment>> HandleAsync(EnrollAgentCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.EnrollmentToken))
        {
            return ServerErrors.InvalidEnrollmentToken;
        }

        var tokenHash = SecretTokens.Hash(command.EnrollmentToken.Trim());
        var server = await servers.GetByEnrollmentTokenHashAsync(tokenHash, ct);
        if (server is null)
        {
            return ServerErrors.InvalidEnrollmentToken;
        }

        var apiKey = ApiKeys.Generate();
        var enrolled = server.Enroll(tokenHash, command.Host, apiKey.Prefix, apiKey.Hash, time.GetUtcNow());
        if (!enrolled.IsSuccess)
        {
            return enrolled.Error;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Another request used the same token first.
            return ServerErrors.InvalidEnrollmentToken;
        }

        return new AgentEnrollment(server.Id, server.Name, apiKey.Key);
    }
}
