using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CriminalStatusAndMurderPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CriminalUntil",
                table: "Characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "MurderPoints",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CriminalUntil",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "MurderPoints",
                table: "Characters");
        }
    }
}
