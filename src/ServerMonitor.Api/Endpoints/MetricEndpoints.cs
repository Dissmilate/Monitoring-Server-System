using Microsoft.AspNetCore.Http.HttpResults;
using ServerMonitor.Api.Auth;
using ServerMonitor.Application.Metrics;
using ServerMonitor.Contracts.Live;
using ServerMonitor.Contracts.Metrics;

namespace ServerMonitor.Api.Endpoints;

internal static class MetricEndpoints
{
    public static IEndpointRouteBuilder MapMetricEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/servers/{id:guid}/metrics", GetAsync)
            .WithTags("Metrics")
            .WithSummary("Metrics of a server for a time range")
            .WithDescription(
                "Defaults to the last hour. resolution = auto (default), raw, 1m or 1h; auto picks raw up to 3 hours, " +
                "1-minute buckets up to 3 days and hourly buckets beyond.")
            .RequireAuthorization(Policies.Viewer);

        api.MapGet("/metrics/latest", GetLatestAsync)
            .WithTags("Metrics")
            .WithSummary("The newest sample of every server that reported in the last 15 minutes")
            .RequireAuthorization(Policies.Viewer);

        return api;
    }

    private static async Task<Ok<IReadOnlyList<ServerSummaryDto>>> GetLatestAsync(
        GetLatestMetricsHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(ct));

    private static async Task<Results<Ok<MetricsDto>, ProblemHttpResult>> GetAsync(
        Guid id,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? resolution,
        GetServerMetricsHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetServerMetricsQuery(id, from, to, resolution), ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
