using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class TravelDestinationKeyedByAnswerPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations");

            migrationBuilder.AlterColumn<uint>(
                name: "Cost",
                table: "TravelDestinations",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "AnswerPath",
                table: "TravelDestinations",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations",
                columns: new[] { "NpcName", "AnswerPath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations");

            migrationBuilder.DropColumn(
                name: "AnswerPath",
                table: "TravelDestinations");

            migrationBuilder.AlterColumn<uint>(
                name: "Cost",
                table: "TravelDestinations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u,
                oldClrType: typeof(uint),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations",
                columns: new[] { "NpcName", "DestinationMapId" });
        }
    }
}
