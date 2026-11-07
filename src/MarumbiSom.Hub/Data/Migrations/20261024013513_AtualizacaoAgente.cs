using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarumbiSom.Hub.Data.Migrations
{
    /// <inheritdoc />
    public partial class AtualizacaoAgente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "update_at",
                table: "store_state",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "update_done_bytes",
                table: "store_state",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_error",
                table: "store_state",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_stage",
                table: "store_state",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "update_total_bytes",
                table: "store_state",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_version",
                table: "store_state",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_releases",
                columns: table => new
                {
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    signature = table.Column<string>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    is_current = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_releases", x => x.version);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_releases");

            migrationBuilder.DropColumn(
                name: "update_at",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "update_done_bytes",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "update_error",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "update_stage",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "update_total_bytes",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "update_version",
                table: "store_state");
        }
    }
}
