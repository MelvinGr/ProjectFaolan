using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Faolan.Migrations
{
    public partial class _1 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                "LastConnection",
                "Characters",
                "TEXT",
                nullable: false,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTime>(
                "CreationDate",
                "Characters",
                "TEXT",
                nullable: false,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTime>(
                "LastConnection",
                "Accounts",
                "TEXT",
                nullable: false,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTime>(
                "CreationDate",
                "Accounts",
                "TEXT",
                nullable: false,
                oldClrType: typeof(uint),
                oldType: "INTEGER");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<uint>(
                "LastConnection",
                "Characters",
                "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<uint>(
                "CreationDate",
                "Characters",
                "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<uint>(
                "LastConnection",
                "Accounts",
                "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<uint>(
                "CreationDate",
                "Accounts",
                "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "TEXT");
        }
    }
}