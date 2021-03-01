using Microsoft.EntityFrameworkCore.Migrations;

namespace Faolan.Migrations
{
    public partial class _6 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                "FK_Npcs_Maps_MapId",
                "Npcs");

            migrationBuilder.AlterColumn<uint>(
                "MapId",
                "Npcs",
                "INTEGER",
                nullable: false,
                defaultValue: 0u,
                oldClrType: typeof(uint),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                "FK_Npcs_Maps_MapId",
                "Npcs",
                "MapId",
                "Maps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                "FK_Npcs_Maps_MapId",
                "Npcs");

            migrationBuilder.AlterColumn<uint>(
                "MapId",
                "Npcs",
                "INTEGER",
                nullable: true,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AddForeignKey(
                "FK_Npcs_Maps_MapId",
                "Npcs",
                "MapId",
                "Maps",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}