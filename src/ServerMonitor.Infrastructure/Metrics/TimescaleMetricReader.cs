using Npgsql;
using ServerMonitor.Application.Metrics;
using ServerMonitor.Contracts.Live;
using ServerMonitor.Contracts.Metrics;

namespace ServerMonitor.Infrastructure.Metrics;

internal sealed class TimescaleMetricReader(NpgsqlDataSource dataSource) : IMetricReader
{
    public async Task<MetricsDto> ReadAsync(
        Guid serverId,
        DateTimeOffset from,
        DateTimeOffset to,
        MetricResolution resolution,
        CancellationToken ct)
    {
        var source = MetricSource.For(resolution);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var system = await ReadSystemAsync(connection, source, serverId, from, to, ct);
        var disks = await ReadDisksAsync(connection, source, serverId, from, to, ct);
        var network = await ReadNetworkAsync(connection, source, serverId, from, to, ct);

        return new MetricsDto(source.Name, from, to, system, disks, network);
    }

    public async Task<IReadOnlyList<ServerSummaryDto>> ReadLatestAsync(DateTimeOffset since, CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT ON (m.server_id)
                   m.server_id, m.time, m.cpu_percent::float8, m.memory_used_bytes, m.memory_total_bytes,
                   (SELECT max(d.used_bytes::float8 / NULLIF(d.total_bytes, 0)) * 100
                    FROM disk_metrics d
                    WHERE d.server_id = m.server_id AND d.time = m.time)
            FROM metrics m
            WHERE m.time >= @since
            ORDER BY m.server_id, m.time DESC
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("since", since.ToUniversalTime());

        var result = new List<ServerSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ServerSummaryDto(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetDouble(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }

        return result;
    }

    private static async Task<List<SystemMetricDto>> ReadSystemAsync(
        NpgsqlConnection connection, MetricSource source, Guid serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $"""
            SELECT {source.Time}, cpu_percent::float8, {source.CpuMax}::float8, memory_used_bytes, memory_total_bytes,
                   swap_used_bytes, load_average_1m::float8, uptime_seconds
            FROM {source.SystemTable}
            WHERE server_id = @server AND {source.Time} >= @from AND {source.Time} < @to
            ORDER BY {source.Time}
            """;

        var result = new List<SystemMetricDto>();
        await using var reader = await Query(connection, sql, serverId, from, to).ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SystemMetricDto(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.GetInt64(7)));
        }

        return result;
    }

    private static async Task<List<DiskSeriesDto>> ReadDisksAsync(
        NpgsqlConnection connection, MetricSource source, Guid serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $"""
            SELECT mount, {source.Time}, used_bytes, total_bytes
            FROM {source.DiskTable}
            WHERE server_id = @server AND {source.Time} >= @from AND {source.Time} < @to
            ORDER BY mount, {source.Time}
            """;

        var series = new List<DiskSeriesDto>();
        await using var reader = await Query(connection, sql, serverId, from, to).ExecuteReaderAsync(ct);
        List<DiskPointDto>? points = null;
        while (await reader.ReadAsync(ct))
        {
            var mount = reader.GetString(0);
            if (series.Count == 0 || series[^1].Mount != mount)
            {
                points = [];
                series.Add(new DiskSeriesDto(mount, points));
            }

            points!.Add(new DiskPointDto(reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        return series;
    }

    private static async Task<List<NetworkSeriesDto>> ReadNetworkAsync(
        NpgsqlConnection connection, MetricSource source, Guid serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $"""
            SELECT interface, {source.Time}, rx_bytes_per_second, tx_bytes_per_second
            FROM {source.NetworkTable}
            WHERE server_id = @server AND {source.Time} >= @from AND {source.Time} < @to
            ORDER BY interface, {source.Time}
            """;

        var series = new List<NetworkSeriesDto>();
        await using var reader = await Query(connection, sql, serverId, from, to).ExecuteReaderAsync(ct);
        List<NetworkPointDto>? points = null;
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (series.Count == 0 || series[^1].Interface != name)
            {
                points = [];
                series.Add(new NetworkSeriesDto(name, points));
            }

            points!.Add(new NetworkPointDto(reader.GetFieldValue<DateTimeOffset>(1), reader.GetDouble(2), reader.GetDouble(3)));
        }

        return series;
    }

    private static NpgsqlCommand Query(NpgsqlConnection connection, string sql, Guid serverId, DateTimeOffset from, DateTimeOffset to) =>
        new(sql, connection)
        {
            Parameters =
            {
                new("server", serverId),
                new("from", from.ToUniversalTime()),
                new("to", to.ToUniversalTime()),
            },
        };

    // Table names are constants, never user input.
    private sealed record MetricSource(
        string Name,
        string SystemTable,
        string DiskTable,
        string NetworkTable,
        string Time,
        string CpuMax)
    {
        private static readonly MetricSource Raw =
            new("raw", "metrics", "disk_metrics", "network_metrics", "time", "cpu_percent");

        private static readonly MetricSource Minute =
            new("1m", "metrics_1m", "disk_metrics_1m", "network_metrics_1m", "bucket", "cpu_percent_max");

        private static readonly MetricSource Hour =
            new("1h", "metrics_1h", "disk_metrics_1h", "network_metrics_1h", "bucket", "cpu_percent_max");

        public static MetricSource For(MetricResolution resolution) => resolution switch
        {
            MetricResolution.Raw => Raw,
            MetricResolution.Minute => Minute,
            MetricResolution.Hour => Hour,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null),
        };
    }
}
