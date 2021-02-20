using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Faolan.Migrations
{
    public partial class _5 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Npcs_WorldMaps_MapId",
                table: "Npcs");

            migrationBuilder.RenameColumn(
                name: "LastIPAddress",
                table: "Characters",
                newName: "LastIpAddress");

            migrationBuilder.RenameColumn(
                name: "LastIPAddress",
                table: "Accounts",
                newName: "LastIpAddress");

            migrationBuilder.AlterColumn<uint>(
                name: "MapId",
                table: "Npcs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u,
                oldClrType: typeof(uint),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastConnection",
                table: "Characters",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastConnection",
                table: "Accounts",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "TEXT");

            migrationBuilder.AddForeignKey(
                name: "FK_Npcs_WorldMaps_MapId",
                table: "Npcs",
                column: "MapId",
                principalTable: "WorldMaps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Npcs_WorldMaps_MapId",
                table: "Npcs");

            migrationBuilder.RenameColumn(
                name: "LastIpAddress",
                table: "Characters",
                newName: "LastIPAddress");

            migrationBuilder.RenameColumn(
                name: "LastIpAddress",
                table: "Accounts",
                newName: "LastIPAddress");

            migrationBuilder.AlterColumn<uint>(
                name: "MapId",
                table: "Npcs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastConnection",
                table: "Characters",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastConnection",
                table: "Accounts",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Npcs_WorldMaps_MapId",
                table: "Npcs",
                column: "MapId",
                principalTable: "WorldMaps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
