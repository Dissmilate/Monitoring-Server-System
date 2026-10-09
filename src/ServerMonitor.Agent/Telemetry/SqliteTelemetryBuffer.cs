using Google.Protobuf;
using Microsoft.Data.Sqlite;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Telemetry;

internal sealed record BufferedSample(long Id, MetricSample Sample);

internal interface ITelemetryBuffer
{
    Task AppendAsync(MetricSample sample, CancellationToken ct);

    Task<IReadOnlyList<BufferedSample>> PeekAsync(int max, CancellationToken ct);

    Task RemoveThroughAsync(long id, CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);

    Task<bool> WaitForDataAsync(TimeSpan timeout, CancellationToken ct);
}

internal sealed partial class SqliteTelemetryBuffer : ITelemetryBuffer, IDisposable
{
    private readonly string _connectionString;
    private readonly int _capacity;
    private readonly ILogger<SqliteTelemetryBuffer> _logger;
    private readonly SemaphoreSlim _dataAvailable = new(0, 1);

    public SqliteTelemetryBuffer(string directory, int capacity, ILogger<SqliteTelemetryBuffer> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Directory.CreateDirectory(directory);

        _capacity = capacity;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "telemetry-buffer.db"),
            Pooling = true,
        }.ToString();

        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS metric_samples (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                payload BLOB    NOT NULL
            );
            """);
    }

    public async Task AppendAsync(MetricSample sample, CancellationToken ct)
    {
        await using var connection = Open();

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO metric_samples (payload) VALUES ($payload)";
            insert.Parameters.AddWithValue("$payload", sample.ToByteArray());
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var trim = connection.CreateCommand())
        {
            trim.CommandText = "DELETE FROM metric_samples WHERE id <= (SELECT max(id) FROM metric_samples) - $capacity";
            trim.Parameters.AddWithValue("$capacity", _capacity);
            var dropped = await trim.ExecuteNonQueryAsync(ct);
            if (dropped > 0)
            {
                LogDropped(_logger, dropped, _capacity);
            }
        }

        if (_dataAvailable.CurrentCount == 0)
        {
            try
            {
                _dataAvailable.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public async Task<IReadOnlyList<BufferedSample>> PeekAsync(int max, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, payload FROM metric_samples ORDER BY id LIMIT $max";
        command.Parameters.AddWithValue("$max", max);

        var samples = new List<BufferedSample>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new BufferedSample(reader.GetInt64(0), MetricSample.Parser.ParseFrom((byte[])reader[1])));
        }

        return samples;
    }

    public async Task RemoveThroughAsync(long id, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metric_samples WHERE id <= $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM metric_samples";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public Task<bool> WaitForDataAsync(TimeSpan timeout, CancellationToken ct) => _dataAvailable.WaitAsync(timeout, ct);

    public void Dispose()
    {
        _dataAvailable.Dispose();

        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry buffer full ({Capacity} samples); dropped the {Count} oldest")]
    private static partial void LogDropped(ILogger logger, int count, int capacity);
}
