using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarumbiSom.Hub.Data.Migrations
{
    /// <inheritdoc />
    public partial class NumeracaoMusicas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "number",
                table: "tracks",
                type: "INTEGER",
                nullable: true);

            // D31: as músicas que já existem ganham número pela ordem de importação (as excluídas também, para o número
            // não ser reaproveitado) e o número vira o título, menos nas que já foram renomeadas no painel.
            migrationBuilder.Sql("""
                UPDATE tracks SET number = (
                    SELECT r.n FROM (SELECT hash, ROW_NUMBER() OVER (ORDER BY created_at, hash) AS n FROM tracks WHERE kind = 'Music') r
                    WHERE r.hash = tracks.hash)
                WHERE kind = 'Music';
                """);
            migrationBuilder.Sql("""
                UPDATE tracks SET title = printf('%03d', number), artist = NULL
                WHERE kind = 'Music' AND hash NOT IN (
                    SELECT target FROM audit_log
                    WHERE action = 'tracks.update' AND target IS NOT NULL
                      AND (data_json LIKE '%"title":"%' OR data_json LIKE '%"artist":"%'));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_tracks_number",
                table: "tracks",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tracks_number",
                table: "tracks");

            migrationBuilder.DropColumn(
                name: "number",
                table: "tracks");
        }
    }
}
