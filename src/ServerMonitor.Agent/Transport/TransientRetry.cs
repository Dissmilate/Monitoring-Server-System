using Grpc.Core;

namespace ServerMonitor.Agent.Transport;

internal static partial class TransientRetry
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        ILogger logger,
        CancellationToken ct)
    {
        var backoff = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (RpcException ex) when (IsTransient(ex.StatusCode) && !ct.IsCancellationRequested)
            {
                var delay = backoff.Next();
                LogRetrying(logger, attempt, ex.StatusCode, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    public static bool IsTransient(StatusCode code) =>
        code is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway call failed with {StatusCode} (attempt {Attempt}); retrying in {Delay}")]
    private static partial void LogRetrying(ILogger logger, int attempt, StatusCode statusCode, TimeSpan delay);
}
