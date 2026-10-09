using Microsoft.AspNetCore.Http.HttpResults;
using ServerMonitor.Domain.Common;

namespace ServerMonitor.Api.Endpoints;

internal static class ResultExtensions
{
    public static ProblemHttpResult ToProblem(this Error error) =>
        TypedResults.Problem(
            title: error.Message,
            statusCode: error.Type switch
            {
                ErrorType.Validation => StatusCodes.Status400BadRequest,
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError,
            },
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}
