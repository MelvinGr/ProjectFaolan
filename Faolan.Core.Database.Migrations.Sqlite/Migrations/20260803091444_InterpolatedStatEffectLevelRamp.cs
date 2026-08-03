using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InterpolatedStatEffectLevelRamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HighAmount",
                table: "SpellStatEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "HighLevel",
                table: "SpellStatEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "LowLevel",
                table: "SpellStatEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "HighLevel",
                table: "FeatEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HighValue",
                table: "FeatEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "LowLevel",
                table: "FeatEffects",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HighAmount",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "HighLevel",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "LowLevel",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "HighLevel",
                table: "FeatEffects");

            migrationBuilder.DropColumn(
                name: "HighValue",
                table: "FeatEffects");

            migrationBuilder.DropColumn(
                name: "LowLevel",
                table: "FeatEffects");
        }
    }
}
