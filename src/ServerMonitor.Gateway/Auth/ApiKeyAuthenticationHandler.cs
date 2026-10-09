using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ServerMonitor.Application.Agents;

namespace ServerMonitor.Gateway.Auth;

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AgentAuthenticator authenticator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        var schemePrefix = AgentAuth.Scheme + " ";
        if (header is null || !header.StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await authenticator.AuthenticateAsync(header[schemePrefix.Length..].Trim(), Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("Invalid or revoked API key.");
        }

        Claim[] claims =
        [
            new(AgentAuth.ServerIdClaim, identity.ServerId.ToString()),
            new(AgentAuth.KeyIdClaim, identity.KeyId.ToString()),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AgentAuth.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AgentAuth.Scheme));
    }
}
