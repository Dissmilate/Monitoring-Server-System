using Microsoft.AspNetCore.Http.HttpResults;
using ServerMonitor.Api.Auth;
using ServerMonitor.Application.Agents;
using ServerMonitor.Contracts.Agents;

namespace ServerMonitor.Api.Endpoints;

internal static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/servers/{id:guid}")
            .WithTags("Agent control")
            .RequireAuthorization(Policies.Viewer);

        group.MapGet("/configuration", GetConfigurationAsync);
        group.MapPut("/configuration", UpdateConfigurationAsync)
            .WithDescription("Saves the desired agent configuration and pushes it to the agent (now if connected, else on reconnect).")
            .RequireAuthorization(Policies.Admin);

        group.MapGet("/commands", ListCommandsAsync);
        group.MapGet("/commands/{commandId:guid}", GetCommandAsync);
        group.MapPost("/commands", RequestCommandAsync)
            .WithDescription("Queues a command for the agent. Poll the returned location for its status.")
            .RequireAuthorization(Policies.Admin);

        return api;
    }

    private static async Task<Results<Ok<AgentConfigurationDto>, ProblemHttpResult>> GetConfigurationAsync(
        Guid id, GetAgentConfigurationHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<AgentConfigurationDto>, ProblemHttpResult>> UpdateConfigurationAsync(
        Guid id, UpdateAgentConfigurationRequest request, UpdateAgentConfigurationHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, request, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<IReadOnlyList<AgentCommandDto>>, ProblemHttpResult>> ListCommandsAsync(
        Guid id, int? limit, ListAgentCommandsHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, limit, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<AgentCommandDto>, ProblemHttpResult>> GetCommandAsync(
        Guid id, Guid commandId, GetAgentCommandHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, commandId, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Accepted<AgentCommandDto>, ProblemHttpResult>> RequestCommandAsync(
        Guid id, RequestAgentCommandRequest request, RequestAgentCommandHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, request, ct);
        return result.IsSuccess
            ? TypedResults.Accepted($"/api/servers/{id}/commands/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }
}
