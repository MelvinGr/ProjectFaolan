using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpellSummonParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SummonIsCompanion",
                table: "Spells",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<uint>(
                name: "SummonPetBuff",
                table: "Spells",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "SummonPointCost",
                table: "Spells",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SummonSpawnHash",
                table: "Spells",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SummonIsCompanion",
                table: "Spells");

            migrationBuilder.DropColumn(
                name: "SummonPetBuff",
                table: "Spells");

            migrationBuilder.DropColumn(
                name: "SummonPointCost",
                table: "Spells");

            migrationBuilder.DropColumn(
                name: "SummonSpawnHash",
                table: "Spells");
        }
    }
}
