using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestGoalTargetWorldObject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "TargetWorldObjectId",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestGoalDefinitions_TargetWorldObjectId",
                table: "QuestGoalDefinitions",
                column: "TargetWorldObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_QuestGoalDefinitions_WorldObjects_TargetWorldObjectId",
                table: "QuestGoalDefinitions",
                column: "TargetWorldObjectId",
                principalTable: "WorldObjects",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestGoalDefinitions_WorldObjects_TargetWorldObjectId",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_QuestGoalDefinitions_TargetWorldObjectId",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "TargetWorldObjectId",
                table: "QuestGoalDefinitions");
        }
    }
}
