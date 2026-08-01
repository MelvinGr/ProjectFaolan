using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestGoalGatesTargetVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GatesTargetVisibility",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Carry the OLD rule's membership forward, or the column silently turns the gate off for the two targets
            // it currently covers. Until now GetQuestGatedNpcMap DERIVED the gate from "a Kill goal (0) with a
            // TargetName, whose NPC is not a quest giver", so that predicate - not a pair of hardcoded goal ids - is
            // what reproduces today's behaviour on any existing database. New rows default to false, which is the
            // point: #521 fills TargetName mechanically and a population mob must not become hidden + no-respawn.
            migrationBuilder.Sql(
                """
                UPDATE QuestGoalDefinitions SET GatesTargetVisibility = 1
                WHERE GoalType = 0 AND TargetName IS NOT NULL AND TargetName <> ''
                  AND TargetName NOT IN (SELECT NpcName FROM ConversationNodes WHERE GrantQuestId > 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GatesTargetVisibility",
                table: "QuestGoalDefinitions");
        }
    }
}
