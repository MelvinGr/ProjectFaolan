using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestGoalHideMapMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Unknown2",
                table: "QuestGoalDefinitions",
                newName: "HideMapMarker");

            // The old column held the u32 the shifted carve read across the tail - 0x01000000 (16777216) when the
            // flag byte was set - so the rename keeps the rows and this squashes the word to the bool it always was.
            // Quoted identifiers: raw SQL goes in verbatim and Postgres folds an unquoted name to lower case.
            migrationBuilder.Sql("UPDATE \"QuestGoalDefinitions\" SET \"HideMapMarker\" = CASE WHEN \"HideMapMarker\" <> 0 THEN 1 ELSE 0 END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "HideMapMarker",
                table: "QuestGoalDefinitions",
                newName: "Unknown2");

            migrationBuilder.Sql("UPDATE \"QuestGoalDefinitions\" SET \"Unknown2\" = CASE WHEN \"Unknown2\" <> 0 THEN 16777216 ELSE 0 END;");
        }
    }
}
