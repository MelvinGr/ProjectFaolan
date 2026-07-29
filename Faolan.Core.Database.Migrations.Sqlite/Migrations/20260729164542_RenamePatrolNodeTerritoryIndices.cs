using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RenamePatrolNodeTerritoryIndices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PathPacked2",
                table: "PatrolNodes",
                newName: "TerritoryPathIndex");

            migrationBuilder.RenameColumn(
                name: "PathPacked1",
                table: "PatrolNodes",
                newName: "TerritoryIndex");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TerritoryPathIndex",
                table: "PatrolNodes",
                newName: "PathPacked2");

            migrationBuilder.RenameColumn(
                name: "TerritoryIndex",
                table: "PatrolNodes",
                newName: "PathPacked1");
        }
    }
}
