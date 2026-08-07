using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpellCriticalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "CriticalChanceHundredths",
                table: "Spells",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "CriticalDamageHundredths",
                table: "Spells",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CriticalChanceHundredths",
                table: "Spells");

            migrationBuilder.DropColumn(
                name: "CriticalDamageHundredths",
                table: "Spells");
        }
    }
}
