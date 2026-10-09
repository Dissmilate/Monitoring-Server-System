using Google.Protobuf;
using Microsoft.Data.Sqlite;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Logs;

internal sealed record QueuedLogEntry(long Id, LogEntry Entry);

internal interface ILogQueue
{
    Task<string?> GetPositionAsync(string source, CancellationToken ct);

    // Entries and the new position are saved in one transaction.
    Task AppendAsync(string source, IReadOnlyList<LogEntry> entries, string position, CancellationToken ct);

    Task<IReadOnlyList<QueuedLogEntry>> PeekAsync(int maxEntries, int maxBytes, CancellationToken ct);

    Task RemoveThroughAsync(long id, CancellationToken ct);

    Task<bool> WaitForDataAsync(TimeSpan timeout, CancellationToken ct);
}

internal sealed partial class SqliteLogQueue : ILogQueue, IDisposable
{
    private readonly string _connectionString;
    private readonly int _capacity;
    private readonly ILogger<SqliteLogQueue> _logger;
    private readonly SemaphoreSlim _dataAvailable = new(0, 1);

    public SqliteLogQueue(string directory, int capacity, ILogger<SqliteLogQueue> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Directory.CreateDirectory(directory);

        _capacity = capacity;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "log-buffer.db"),
            Pooling = true,
        }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS log_entries (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                payload BLOB    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS log_positions (
                source   TEXT PRIMARY KEY,
                position TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<string?> GetPositionAsync(string source, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT position FROM log_positions WHERE source = $source";
        command.Parameters.AddWithValue("$source", source);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    public async Task AppendAsync(string source, IReadOnlyList<LogEntry> entries, string position, CancellationToken ct)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO log_entries (payload) VALUES ($payload)";
            var payload = insert.Parameters.Add("$payload", SqliteType.Blob);
            foreach (var entry in entries)
            {
                payload.Value = entry.ToByteArray();
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO log_positions (source, position) VALUES ($source, $position)
                ON CONFLICT (source) DO UPDATE SET position = excluded.position
                """;
            upsert.Parameters.AddWithValue("$source", source);
            upsert.Parameters.AddWithValue("$position", position);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        int dropped;
        await using (var trim = connection.CreateCommand())
        {
            trim.CommandText = "DELETE FROM log_entries WHERE id <= (SELECT max(id) FROM log_entries) - $capacity";
            trim.Parameters.AddWithValue("$capacity", _capacity);
            dropped = await trim.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        if (dropped > 0)
        {
            LogDropped(_logger, dropped, _capacity);
        }

        if (entries.Count > 0 && _dataAvailable.CurrentCount == 0)
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

    public async Task<IReadOnlyList<QueuedLogEntry>> PeekAsync(int maxEntries, int maxBytes, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, payload FROM log_entries ORDER BY id LIMIT $max";
        command.Parameters.AddWithValue("$max", maxEntries);

        var entries = new List<QueuedLogEntry>();
        var bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var payload = (byte[])reader[1];
            bytes += payload.Length;
            if (entries.Count > 0 && bytes > maxBytes)
            {
                break;
            }

            entries.Add(new QueuedLogEntry(reader.GetInt64(0), LogEntry.Parser.ParseFrom(payload)));
        }

        return entries;
    }

    public async Task RemoveThroughAsync(long id, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM log_entries WHERE id <= $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log buffer full ({Capacity} entries); dropped the {Count} oldest")]
    private static partial void LogDropped(ILogger logger, int count, int capacity);
}
