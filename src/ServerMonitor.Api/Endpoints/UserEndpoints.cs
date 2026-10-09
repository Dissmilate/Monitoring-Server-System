using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Api.Auth;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Contracts.Users;
using ServerMonitor.Infrastructure.Identity;

namespace ServerMonitor.Api.Endpoints;

internal static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/users")
            .WithTags("Users")
            .RequireAuthorization(Policies.Admin);

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return api;
    }

    private static async Task<Ok<List<UserDto>>> ListAsync(UserManager<AppUser> userManager, CancellationToken ct)
    {
        var users = await userManager.Users.OrderBy(u => u.Email).ToListAsync(ct);
        var result = new List<UserDto>(users.Count);
        foreach (var user in users)
        {
            var roles = await userManager.GetRolesAsync(user);
            result.Add(new UserDto(user.Id, user.Email!, roles.FirstOrDefault() ?? string.Empty));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Created<UserDto>, ValidationProblem>> CreateAsync(
        CreateUserRequest request, UserManager<AppUser> userManager)
    {
        if (!Roles.All.Contains(request.Role))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = [$"Role must be one of: {string.Join(", ", Roles.All)}."],
            });
        }

        var user = new AppUser { UserName = request.Email, Email = request.Email, EmailConfirmed = true };
        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return TypedResults.ValidationProblem(created.Errors
                .GroupBy(e => e.Code)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        await userManager.AddToRoleAsync(user, request.Role);
        return TypedResults.Created($"/api/users/{user.Id}", new UserDto(user.Id, user.Email, request.Role));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid id, ClaimsPrincipal principal, UserManager<AppUser> userManager)
    {
        if (userManager.GetUserId(principal) == id.ToString())
        {
            return TypedResults.Problem(title: "You cannot delete your own account.", statusCode: StatusCodes.Status409Conflict);
        }

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        await userManager.DeleteAsync(user);
        return TypedResults.NoContent();
    }
}
