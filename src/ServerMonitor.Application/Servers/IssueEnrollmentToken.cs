using Microsoft.Extensions.Options;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Security;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Servers;

public sealed class IssueEnrollmentTokenHandler(
    IServerRepository servers,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    IOptions<EnrollmentOptions> options)
{
    public async Task<Result<EnrollmentTokenDto>> HandleAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(serverId, ct);
        if (server is null)
        {
            return ServerErrors.NotFound(serverId);
        }

        var plaintext = SecretTokens.Generate(SecretTokens.EnrollmentPrefix);
        var token = server.IssueEnrollmentToken(
            SecretTokens.Hash(plaintext),
            time.GetUtcNow(),
            options.Value.TokenLifetime);

        await unitOfWork.SaveChangesAsync(ct);
        return new EnrollmentTokenDto(plaintext, token.ExpiresAt);
    }
}
