using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Infrastructure.Identity;

namespace ServerMonitor.Api.Endpoints;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapGet("/me", GetCurrentUserAsync).RequireAuthorization();

        return api;
    }

    private static async Task<Results<Ok<CurrentUserDto>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        SignInManager<AppUser> signInManager,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return InvalidCredentials();
        }

        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: true, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return TypedResults.Problem(
                title: "Account is temporarily locked due to failed login attempts.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!result.Succeeded)
        {
            return InvalidCredentials();
        }

        return TypedResults.Ok(await ToDtoAsync(user, userManager));
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUserDto>, UnauthorizedHttpResult>> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(await ToDtoAsync(user, userManager));
    }

    private static ProblemHttpResult InvalidCredentials() =>
        TypedResults.Problem(title: "Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<CurrentUserDto> ToDtoAsync(AppUser user, UserManager<AppUser> userManager) =>
        new(user.Id, user.Email!, [.. await userManager.GetRolesAsync(user)]);
}
