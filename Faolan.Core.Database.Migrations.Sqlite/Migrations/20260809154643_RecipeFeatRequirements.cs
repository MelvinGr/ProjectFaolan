using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RecipeFeatRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RefId1",
                table: "Recipes",
                newName: "RequiredWorkshopFeatId");

            migrationBuilder.RenameColumn(
                name: "RefId0",
                table: "Recipes",
                newName: "RequiredFeatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RequiredWorkshopFeatId",
                table: "Recipes",
                newName: "RefId1");

            migrationBuilder.RenameColumn(
                name: "RequiredFeatId",
                table: "Recipes",
                newName: "RefId0");
        }
    }
}
