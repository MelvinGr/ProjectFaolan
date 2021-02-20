using Microsoft.EntityFrameworkCore.Migrations;

namespace Faolan.Migrations
{
    public partial class _4 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                "FK_Npcs_WorldMaps_WorldMapId",
                "Npcs");

            migrationBuilder.RenameColumn(
                "WorldMapId",
                "Npcs",
                "MapId");

            migrationBuilder.RenameIndex(
                "IX_Npcs_WorldMapId",
                table: "Npcs",
                newName: "IX_Npcs_MapId");

            migrationBuilder.RenameColumn(
                "CurrentMapId",
                "Characters",
                "MapId");

            migrationBuilder.CreateIndex(
                "IX_Characters_MapId",
                "Characters",
                "MapId");

            migrationBuilder.AddForeignKey(
                "FK_Characters_WorldMaps_MapId",
                "Characters",
                "MapId",
                "WorldMaps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                "FK_Npcs_WorldMaps_MapId",
                "Npcs",
                "MapId",
                "WorldMaps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                "FK_Characters_WorldMaps_MapId",
                "Characters");

            migrationBuilder.DropForeignKey(
                "FK_Npcs_WorldMaps_MapId",
                "Npcs");

            migrationBuilder.DropIndex(
                "IX_Characters_MapId",
                "Characters");

            migrationBuilder.RenameColumn(
                "MapId",
                "Npcs",
                "WorldMapId");

            migrationBuilder.RenameIndex(
                "IX_Npcs_MapId",
                table: "Npcs",
                newName: "IX_Npcs_WorldMapId");

            migrationBuilder.RenameColumn(
                "MapId",
                "Characters",
                "CurrentMapId");

            migrationBuilder.AddForeignKey(
                "FK_Npcs_WorldMaps_WorldMapId",
                "Npcs",
                "WorldMapId",
                "WorldMaps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}