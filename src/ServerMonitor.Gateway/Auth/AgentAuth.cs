using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace ServerMonitor.Gateway.Auth;

internal static class AgentAuth
{
    public const string Scheme = "ApiKey";
    public const string Policy = "Agent";
    public const string ServerIdClaim = "server_id";
    public const string KeyIdClaim = "agent_key_id";

    public static IServiceCollection AddAgentAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(Scheme, configureOptions: null);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(ServerIdClaim));

        return services;
    }

    public static Guid GetServerId(this ClaimsPrincipal principal) => principal.GetGuidClaim(ServerIdClaim);

    public static Guid GetKeyId(this ClaimsPrincipal principal) => principal.GetGuidClaim(KeyIdClaim);

    private static Guid GetGuidClaim(this ClaimsPrincipal principal, string type) =>
        Guid.Parse(principal.FindFirstValue(type)
            ?? throw new InvalidOperationException("The caller is not an authenticated agent."));
}
