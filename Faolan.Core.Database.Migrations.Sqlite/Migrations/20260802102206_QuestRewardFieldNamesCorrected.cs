using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// HAND-EDITED - do not re-scaffold. `dotnet ef migrations add` mis-paired this the same way it mis-paired
    /// QuestLineRename, and worse: `SnapshotXpReward` exists BOTH before and after this migration but on a
    /// DIFFERENT column (it was @+0xf0, it becomes @+0x28), so EF saw the name as unchanged, left it alone, and
    /// emitted a scrambled set of renames around it. Applying that would have silently moved @+0xf0's values into
    /// the column the model now reads as the XP reward.
    ///
    /// The correct change is a three-way ROTATION, and the order below is load-bearing - freeing the name
    /// `SnapshotXpReward` before reusing it:
    ///     SnapshotXpReward         (@+0xf0) -> SnapshotUnknownF0
    ///     SnapshotCashReward       (@+0xf8) -> SnapshotUnknownF8
    ///     SnapshotQuestLineReward  (@+0x28) -> SnapshotXpReward
    /// </remarks>
    public partial class QuestRewardFieldNamesCorrected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SnapshotXpReward",
                table: "QuestDefinitions",
                newName: "SnapshotUnknownF0");

            migrationBuilder.RenameColumn(
                name: "SnapshotCashReward",
                table: "QuestDefinitions",
                newName: "SnapshotUnknownF8");

            migrationBuilder.RenameColumn(
                name: "SnapshotQuestLineReward",
                table: "QuestDefinitions",
                newName: "SnapshotXpReward");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SnapshotXpReward",
                table: "QuestDefinitions",
                newName: "SnapshotQuestLineReward");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknownF8",
                table: "QuestDefinitions",
                newName: "SnapshotCashReward");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknownF0",
                table: "QuestDefinitions",
                newName: "SnapshotXpReward");
        }
    }
}
