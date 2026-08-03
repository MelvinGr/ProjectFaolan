using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FeatSpellModifierPercentSigned : Migration
    {
        /// <inheritdoc />
        // `FeatSpellModifier.PercentHundredths` went uint -> int (#591): the magnitude IS signed - `Set's Chosen`
        // steps -2000/-4000/-6000/-8000/-10000 across its five ranks. SQLite stores both as INTEGER, so there is no
        // schema change to make and the whole migration is the DATA: the 11 rows that were written as the unsigned
        // wrap of a negative have to come back, or the reader throws reading 4294962296 into an int.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE FeatSpellModifiers SET PercentHundredths = PercentHundredths - 4294967296 WHERE PercentHundredths > 2147483647;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE FeatSpellModifiers SET PercentHundredths = PercentHundredths + 4294967296 WHERE PercentHundredths < 0;");
        }
    }
}
