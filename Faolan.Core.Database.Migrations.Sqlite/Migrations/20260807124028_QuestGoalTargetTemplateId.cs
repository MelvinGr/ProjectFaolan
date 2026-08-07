using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestGoalTargetTemplateId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "TargetTemplateId",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestGoalDefinitions_TargetTemplateId",
                table: "QuestGoalDefinitions",
                column: "TargetTemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_QuestGoalDefinitions_NpcTemplates_TargetTemplateId",
                table: "QuestGoalDefinitions",
                column: "TargetTemplateId",
                principalTable: "NpcTemplates",
                principalColumn: "TemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestGoalDefinitions_NpcTemplates_TargetTemplateId",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_QuestGoalDefinitions_TargetTemplateId",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "TargetTemplateId",
                table: "QuestGoalDefinitions");
        }
    }
}
