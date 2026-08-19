using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FeatEffectValueSigned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No SCHEMA change - SQLite stores both a uint and an int as INTEGER, so the scaffold is empty. What is
            // NOT empty is the DATA: `FeatEffect.Value` went from uint to int (#863) and 44 shipped rows hold a word
            // the client states as negative and we stored as its 2^32 complement - `Pet Mastery`'s Pet Damage
            // Coefficient ladder, the caster classes' Weapon Damage Ranged, `Mind Phase`, `Subtlety`. Reading one of
            // those into an int overflows, so an existing database has to be repaired here rather than only by a
            // re-import; the reader would throw before it ever got the chance.
            migrationBuilder.Sql("UPDATE FeatEffects SET Value = Value - 4294967296 WHERE Value >= 2147483648;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE FeatEffects SET Value = Value + 4294967296 WHERE Value < 0;");
        }
    }
}
