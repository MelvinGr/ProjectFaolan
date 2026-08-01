using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RenameIndexGoalStepsToAreaQuestIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IndexGoalSteps",
                table: "QuestDefinitions",
                newName: "IndexAreaQuestIds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IndexAreaQuestIds",
                table: "QuestDefinitions",
                newName: "IndexGoalSteps");
        }
    }
}
