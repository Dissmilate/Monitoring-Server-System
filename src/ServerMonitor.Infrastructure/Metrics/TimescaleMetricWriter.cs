using Npgsql;
using NpgsqlTypes;
using ServerMonitor.Application.Metrics;

namespace ServerMonitor.Infrastructure.Metrics;

// COPY into a temp table, then INSERT ... ON CONFLICT DO NOTHING.
internal sealed class TimescaleMetricWriter(NpgsqlDataSource dataSource) : IMetricWriter
{
    private const string SystemColumns =
        "time, server_id, cpu_percent, memory_used_bytes, memory_total_bytes, swap_used_bytes, load_average_1m, uptime_seconds";

    private const string DiskColumns = "time, server_id, mount, used_bytes, total_bytes";

    private const string NetworkColumns = "time, server_id, interface, rx_bytes_per_second, tx_bytes_per_second";

    public async Task WriteAsync(MetricWriteBatch batch, CancellationToken ct)
    {
        if (batch.IsEmpty)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await CopyAndMergeAsync(connection, "metrics", SystemColumns, batch.System, WriteSystemAsync, ct);
        await CopyAndMergeAsync(connection, "disk_metrics", DiskColumns, batch.Disks, WriteDiskAsync, ct);
        await CopyAndMergeAsync(connection, "network_metrics", NetworkColumns, batch.Network, WriteNetworkAsync, ct);

        await transaction.CommitAsync(ct);
    }

    private static async Task CopyAndMergeAsync<T>(
        NpgsqlConnection connection,
        string table,
        string columns,
        IReadOnlyList<T> rows,
        Func<NpgsqlBinaryImporter, T, CancellationToken, Task> writeRow,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var staging = $"staging_{table}";
        await using (var create = new NpgsqlCommand(
            $"CREATE TEMP TABLE {staging} (LIKE {table} INCLUDING DEFAULTS) ON COMMIT DROP", connection))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var importer = await connection.BeginBinaryImportAsync(
            $"COPY {staging} ({columns}) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var row in rows)
            {
                await importer.StartRowAsync(ct);
                await writeRow(importer, row, ct);
            }

            await importer.CompleteAsync(ct);
        }

        await using var merge = new NpgsqlCommand(
            $"INSERT INTO {table} ({columns}) SELECT {columns} FROM {staging} ON CONFLICT DO NOTHING", connection);
        await merge.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteSystemAsync(NpgsqlBinaryImporter importer, SystemMetricPoint point, CancellationToken ct)
    {
        await importer.WriteAsync(point.Time, NpgsqlDbType.TimestampTz, ct);
        await importer.WriteAsync(point.ServerId, NpgsqlDbType.Uuid, ct);
        await importer.WriteAsync((float)point.CpuPercent, NpgsqlDbType.Real, ct);
        await importer.WriteAsync(point.MemoryUsedBytes, NpgsqlDbType.Bigint, ct);
        await importer.WriteAsync(point.MemoryTotalBytes, NpgsqlDbType.Bigint, ct);
        await importer.WriteAsync(point.SwapUsedBytes, NpgsqlDbType.Bigint, ct);
        if (point.LoadAverage1m is { } load)
        {
            await importer.WriteAsync((float)load, NpgsqlDbType.Real, ct);
        }
        else
        {
            await importer.WriteNullAsync(ct);
        }

        await importer.WriteAsync(point.UptimeSeconds, NpgsqlDbType.Bigint, ct);
    }

    private static async Task WriteDiskAsync(NpgsqlBinaryImporter importer, DiskMetricPoint point, CancellationToken ct)
    {
        await importer.WriteAsync(point.Time, NpgsqlDbType.TimestampTz, ct);
        await importer.WriteAsync(point.ServerId, NpgsqlDbType.Uuid, ct);
        await importer.WriteAsync(point.Mount, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(point.UsedBytes, NpgsqlDbType.Bigint, ct);
        await importer.WriteAsync(point.TotalBytes, NpgsqlDbType.Bigint, ct);
    }

    private static async Task WriteNetworkAsync(NpgsqlBinaryImporter importer, NetworkMetricPoint point, CancellationToken ct)
    {
        await importer.WriteAsync(point.Time, NpgsqlDbType.TimestampTz, ct);
        await importer.WriteAsync(point.ServerId, NpgsqlDbType.Uuid, ct);
        await importer.WriteAsync(point.Interface, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(point.RxBytesPerSecond, NpgsqlDbType.Double, ct);
        await importer.WriteAsync(point.TxBytesPerSecond, NpgsqlDbType.Double, ct);
    }
}
