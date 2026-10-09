namespace ServerMonitor.IntegrationTests.Infrastructure;

internal static class Eventually
{
    public static async Task<T> GetAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> isDone,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var value = await probe();
            if (isDone(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within the timeout. Last value: {value}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
