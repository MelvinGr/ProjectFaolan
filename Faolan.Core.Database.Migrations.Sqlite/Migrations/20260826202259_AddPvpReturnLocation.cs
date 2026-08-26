using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddPvpReturnLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "PvpReturnMapId",
                table: "Characters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "PvpReturnX",
                table: "Characters",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "PvpReturnY",
                table: "Characters",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "PvpReturnZ",
                table: "Characters",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PvpReturnMapId",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "PvpReturnX",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "PvpReturnY",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "PvpReturnZ",
                table: "Characters");
        }
    }
}
