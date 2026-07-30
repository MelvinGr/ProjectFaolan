using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestGoalPreconditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "RequiredLevel",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "RequiredRank",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredRankTitle",
                table: "QuestGoalDefinitions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiredLevel",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "RequiredRank",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "RequiredRankTitle",
                table: "QuestGoalDefinitions");
        }
    }
}
