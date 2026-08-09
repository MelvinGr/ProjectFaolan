using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpellStatEffectPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects");

            migrationBuilder.AddColumn<uint>(
                name: "PeriodCentiseconds",
                table: "SpellStatEffects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects",
                columns: new[] { "SpellId", "StatId", "PeriodCentiseconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "PeriodCentiseconds",
                table: "SpellStatEffects");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects",
                columns: new[] { "SpellId", "StatId" });
        }
    }
}
