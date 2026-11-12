using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarumbiSom.Hub.Data.Migrations
{
    /// <inheritdoc />
    public partial class PastasOfertas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "playlist_stores",
                columns: table => new
                {
                    playlist_id = table.Column<int>(type: "INTEGER", nullable: false),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_stores", x => new { x.playlist_id, x.store_id });
                    table.ForeignKey(
                        name: "FK_playlist_stores_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_playlist_stores_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_stores_store_id",
                table: "playlist_stores",
                column: "store_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "playlist_stores");
        }
    }
}
