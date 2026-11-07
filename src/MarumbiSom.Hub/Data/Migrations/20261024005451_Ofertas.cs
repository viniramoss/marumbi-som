using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarumbiSom.Hub.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ofertas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "next_offer_at",
                table: "store_state",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "next_offer_key",
                table: "store_state",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "next_offer_at",
                table: "store_state");

            migrationBuilder.DropColumn(
                name: "next_offer_key",
                table: "store_state");
        }
    }
}
