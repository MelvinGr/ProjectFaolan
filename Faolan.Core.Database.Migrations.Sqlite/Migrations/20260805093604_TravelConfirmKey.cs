using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class TravelConfirmKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "TravelConfirmKey1",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TravelConfirmKey2",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TravelConfirmKey3",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TravelConfirmKey4",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TravelConfirmKey1",
                table: "Maps");

            migrationBuilder.DropColumn(
                name: "TravelConfirmKey2",
                table: "Maps");

            migrationBuilder.DropColumn(
                name: "TravelConfirmKey3",
                table: "Maps");

            migrationBuilder.DropColumn(
                name: "TravelConfirmKey4",
                table: "Maps");
        }
    }
}
