using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarumbiSom.Hub.Data.Migrations
{
    /// <inheritdoc />
    public partial class Painel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "bt_changed_at",
                table: "store_state",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "playlist_key",
                table: "store_state",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "position_at",
                table: "store_state",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "queue_json",
                table: "store_state",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "sync_changed_at",
                table: "store_state",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bt_changed_at",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "playlist_key",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "position_at",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "queue_json",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "sync_changed_at",
                table: "store_state");
        }
    }
}
