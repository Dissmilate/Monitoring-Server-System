using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerMonitor.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// TimescaleDB storage for metrics. These tables are written and read with raw SQL
    /// (binary COPY / continuous aggregates), so they are not part of the EF Core model.
    /// </summary>
    public partial class MetricsHypertables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");

            // Raw samples. The primary key makes re-delivered samples idempotent (ON CONFLICT DO NOTHING).
            migrationBuilder.Sql("""
                CREATE TABLE metrics (
                    time               timestamptz NOT NULL,
                    server_id          uuid        NOT NULL,
                    cpu_percent        real        NOT NULL,
                    memory_used_bytes  bigint      NOT NULL,
                    memory_total_bytes bigint      NOT NULL,
                    swap_used_bytes    bigint      NOT NULL,
                    load_average_1m    real        NULL,
                    uptime_seconds     bigint      NOT NULL,
                    PRIMARY KEY (server_id, time)
                );
                SELECT create_hypertable('metrics', by_range('time', INTERVAL '1 day'));

                CREATE TABLE disk_metrics (
                    time        timestamptz NOT NULL,
                    server_id   uuid        NOT NULL,
                    mount       text        NOT NULL,
                    used_bytes  bigint      NOT NULL,
                    total_bytes bigint      NOT NULL,
                    PRIMARY KEY (server_id, mount, time)
                );
                SELECT create_hypertable('disk_metrics', by_range('time', INTERVAL '1 day'));

                CREATE TABLE network_metrics (
                    time                timestamptz      NOT NULL,
                    server_id           uuid             NOT NULL,
                    interface           text             NOT NULL,
                    rx_bytes_per_second double precision NOT NULL,
                    tx_bytes_per_second double precision NOT NULL,
                    PRIMARY KEY (server_id, interface, time)
                );
                SELECT create_hypertable('network_metrics', by_range('time', INTERVAL '1 day'));
                """);

            // Continuous aggregates: 1-minute and 1-hour buckets, both computed from raw data (averages of
            // averages would be skewed). materialized_only = false merges not-yet-materialized recent data.
            foreach (var (suffix, bucket) in new[] { ("1m", "1 minute"), ("1h", "1 hour") })
            {
                migrationBuilder.Sql($"""
                    CREATE MATERIALIZED VIEW metrics_{suffix}
                    WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
                    SELECT server_id,
                           time_bucket(INTERVAL '{bucket}', time) AS bucket,
                           avg(cpu_percent)::real               AS cpu_percent,
                           max(cpu_percent)                     AS cpu_percent_max,
                           avg(memory_used_bytes)::bigint       AS memory_used_bytes,
                           max(memory_total_bytes)              AS memory_total_bytes,
                           avg(swap_used_bytes)::bigint         AS swap_used_bytes,
                           avg(load_average_1m)::real           AS load_average_1m,
                           max(uptime_seconds)                  AS uptime_seconds
                    FROM metrics
                    GROUP BY server_id, bucket
                    WITH NO DATA;

                    CREATE MATERIALIZED VIEW disk_metrics_{suffix}
                    WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
                    SELECT server_id,
                           mount,
                           time_bucket(INTERVAL '{bucket}', time) AS bucket,
                           avg(used_bytes)::bigint AS used_bytes,
                           max(total_bytes)        AS total_bytes
                    FROM disk_metrics
                    GROUP BY server_id, mount, bucket
                    WITH NO DATA;

                    CREATE MATERIALIZED VIEW network_metrics_{suffix}
                    WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
                    SELECT server_id,
                           interface,
                           time_bucket(INTERVAL '{bucket}', time) AS bucket,
                           avg(rx_bytes_per_second) AS rx_bytes_per_second,
                           avg(tx_bytes_per_second) AS tx_bytes_per_second
                    FROM network_metrics
                    GROUP BY server_id, interface, bucket
                    WITH NO DATA;
                    """);
            }

            // Refresh windows stay inside the raw retention (7 days), so dropping raw chunks never
            // erases already-aggregated history.
            foreach (var prefix in new[] { "metrics", "disk_metrics", "network_metrics" })
            {
                migrationBuilder.Sql($"""
                    SELECT add_continuous_aggregate_policy('{prefix}_1m',
                        start_offset => INTERVAL '2 hours', end_offset => INTERVAL '1 minute',
                        schedule_interval => INTERVAL '1 minute');
                    SELECT add_continuous_aggregate_policy('{prefix}_1h',
                        start_offset => INTERVAL '3 days', end_offset => INTERVAL '1 hour',
                        schedule_interval => INTERVAL '30 minutes');

                    SELECT add_retention_policy('{prefix}', INTERVAL '7 days');
                    SELECT add_retention_policy('{prefix}_1m', INTERVAL '30 days');
                    SELECT add_retention_policy('{prefix}_1h', INTERVAL '365 days');
                    """);
            }

            migrationBuilder.Sql("""
                ALTER TABLE metrics SET (timescaledb.compress,
                    timescaledb.compress_segmentby = 'server_id', timescaledb.compress_orderby = 'time DESC');
                ALTER TABLE disk_metrics SET (timescaledb.compress,
                    timescaledb.compress_segmentby = 'server_id, mount', timescaledb.compress_orderby = 'time DESC');
                ALTER TABLE network_metrics SET (timescaledb.compress,
                    timescaledb.compress_segmentby = 'server_id, interface', timescaledb.compress_orderby = 'time DESC');

                SELECT add_compression_policy('metrics', INTERVAL '1 day');
                SELECT add_compression_policy('disk_metrics', INTERVAL '1 day');
                SELECT add_compression_policy('network_metrics', INTERVAL '1 day');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var prefix in new[] { "metrics", "disk_metrics", "network_metrics" })
            {
                migrationBuilder.Sql($"""
                    DROP MATERIALIZED VIEW IF EXISTS {prefix}_1h;
                    DROP MATERIALIZED VIEW IF EXISTS {prefix}_1m;
                    DROP TABLE IF EXISTS {prefix};
                    """);
            }
        }
    }
}
