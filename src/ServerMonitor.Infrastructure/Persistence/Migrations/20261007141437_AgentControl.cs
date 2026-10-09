using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerMonitor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Edited by hand: the previous concurrency token was PostgreSQL's xmin system column, which
            // cannot be renamed or altered. The explicit version column is simply added.
            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "servers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "config_version",
                table: "servers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "connection_id",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "metrics_interval_seconds",
                table: "servers",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateTable(
                name: "agent_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_commands", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_commands_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_commands_server_id_created_at",
                table: "agent_commands",
                columns: new[] { "server_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_commands_unpublished",
                table: "agent_commands",
                column: "created_at",
                filter: "published_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_commands");

            migrationBuilder.DropColumn(
                name: "config_version",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "connection_id",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "metrics_interval_seconds",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "version",
                table: "servers");
        }
    }
}
