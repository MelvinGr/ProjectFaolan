using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MapAreaClientRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "RegionId",
                table: "MapAreas",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapAreas_RegionId",
                table: "MapAreas",
                column: "RegionId");

            migrationBuilder.AddForeignKey(
                name: "FK_MapAreas_MapRegions_RegionId",
                table: "MapAreas",
                column: "RegionId",
                principalTable: "MapRegions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MapAreas_MapRegions_RegionId",
                table: "MapAreas");

            migrationBuilder.DropIndex(
                name: "IX_MapAreas_RegionId",
                table: "MapAreas");

            migrationBuilder.DropColumn(
                name: "RegionId",
                table: "MapAreas");
        }
    }
}
