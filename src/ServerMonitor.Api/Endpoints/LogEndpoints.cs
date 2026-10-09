using Microsoft.AspNetCore.Http.HttpResults;
using ServerMonitor.Api.Auth;
using ServerMonitor.Application.Logs;
using ServerMonitor.Contracts.Logs;

namespace ServerMonitor.Api.Endpoints;

internal static partial class LogEndpoints
{
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/logs", SearchAsync)
            .WithTags("Logs")
            .WithSummary("Search logs, newest first")
            .WithDescription(
                "Defaults to the last 24 hours. levels: comma-separated (Trace, Debug, Information, Warning, Error, Critical); " +
                "q: full-text search in the message (all words must match); limit: 1-200 (default 50). " +
                "Pass nextCursor back as cursor to get older entries.")
            .RequireAuthorization(Policies.Viewer);

        return api;
    }

    private static async Task<Results<Ok<LogSearchResultDto>, ProblemHttpResult>> SearchAsync(
        Guid? serverId,
        string? levels,
        string? q,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        string? cursor,
        SearchLogsHandler handler,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        try
        {
            var result = await handler.HandleAsync(new SearchLogsRequest(serverId, levels, q, from, to, limit, cursor), ct);
            return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
        }
        catch (LogStoreException ex)
        {
            LogSearchFailed(loggers.CreateLogger(typeof(LogEndpoints)), ex);
            return TypedResults.Problem(title: "Log storage is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Log search failed")]
    private static partial void LogSearchFailed(ILogger logger, Exception exception);
}
