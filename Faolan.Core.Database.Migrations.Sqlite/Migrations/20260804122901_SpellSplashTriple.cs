using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpellSplashTriple : Migration
    {
        // ⚠️ Hand-corrected: the scaffolder paired `DamageRating` with `SplashRadius`, which would have relabelled
        // every existing value as the wrong member of the triple. The old column holds RDB field 348 / 100, and 348
        // is what the client's stat-name table calls Splash Damage (#626) - so the rename that PRESERVES the data is
        // DamageRating -> SplashDamage, with radius (347) and max-targets (349) added as new columns the RDB spell
        // module fills.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DamageRating",
                table: "Spells",
                newName: "SplashDamage");

            migrationBuilder.AddColumn<float>(
                name: "SplashRadius",
                table: "Spells",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<uint>(
                name: "SplashMaxTargets",
                table: "Spells",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SplashRadius",
                table: "Spells");

            migrationBuilder.DropColumn(
                name: "SplashMaxTargets",
                table: "Spells");

            migrationBuilder.RenameColumn(
                name: "SplashDamage",
                table: "Spells",
                newName: "DamageRating");
        }
    }
}
