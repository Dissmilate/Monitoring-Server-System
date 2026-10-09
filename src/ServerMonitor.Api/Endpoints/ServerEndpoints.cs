using Microsoft.AspNetCore.Http.HttpResults;
using ServerMonitor.Api.Auth;
using ServerMonitor.Application.Servers;
using ServerMonitor.Contracts.Servers;

namespace ServerMonitor.Api.Endpoints;

internal static class ServerEndpoints
{
    public static IEndpointRouteBuilder MapServerEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/servers")
            .WithTags("Servers")
            .RequireAuthorization(Policies.Viewer);

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Admin);
        group.MapPut("/{id:guid}", RenameAsync).RequireAuthorization(Policies.Admin);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(Policies.Admin);
        group.MapPost("/{id:guid}/enrollment-tokens", IssueEnrollmentTokenAsync).RequireAuthorization(Policies.Admin);
        group.MapDelete("/{id:guid}/agent-keys", RevokeAgentKeysAsync).RequireAuthorization(Policies.Admin);

        return api;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeAgentKeysAsync(
        Guid id, RevokeAgentKeysHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Ok<IReadOnlyList<ServerDto>>> ListAsync(ListServersHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(ct));

    private static async Task<Results<Ok<ServerDto>, ProblemHttpResult>> GetAsync(
        Guid id, GetServerHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<ServerDto>, ProblemHttpResult>> CreateAsync(
        CreateServerRequest request, CreateServerHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(request, ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/servers/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ServerDto>, ProblemHttpResult>> RenameAsync(
        Guid id, RenameServerRequest request, RenameServerHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, request, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, DeleteServerHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<EnrollmentTokenDto>, ProblemHttpResult>> IssueEnrollmentTokenAsync(
        Guid id, IssueEnrollmentTokenHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
