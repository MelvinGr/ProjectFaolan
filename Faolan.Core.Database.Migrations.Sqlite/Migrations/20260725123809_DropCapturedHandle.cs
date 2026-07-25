using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DropCapturedHandle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapturedHandle",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "CapturedHandle",
                table: "NpcSpawns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "CapturedHandle",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "CapturedHandle",
                table: "NpcSpawns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }
    }
}
