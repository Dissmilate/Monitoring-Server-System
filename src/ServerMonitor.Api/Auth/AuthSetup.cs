using Microsoft.AspNetCore.Identity;
using ServerMonitor.Contracts.Auth;

namespace ServerMonitor.Api.Auth;

internal static class Policies
{
    public const string Admin = "Admin";

    public const string Viewer = "Viewer";
}

internal static class AuthSetup
{
    public static IServiceCollection AddDashboardAuth(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "sm.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;

            // API: return 401/403 instead of redirecting to a login page.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(Policies.Viewer, policy => policy.RequireRole(Roles.Admin, Roles.Viewer));

        return services;
    }
}
