using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MapRegionNoMount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NoMount",
                table: "MapRegions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NoMount",
                table: "MapRegions");
        }
    }
}
