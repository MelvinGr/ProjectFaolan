using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// HAND-EDITED - do not re-scaffold. `dotnet ef migrations add` got SIX of these eight renames wrong: it
    /// matches renamed columns by type and ordinal rather than by intent, so a batch of same-typed columns comes
    /// out crossed. It even paired the two `ulong` columns (SnapshotMoneyReward, SnapshotUnknownF8) into `uint`
    /// ones, which SQLite would have accepted silently. Only SnapshotXpReward and SnapshotChainHeadQuestId
    /// happened to land correctly.
    ///
    /// No name exists on both sides here, so a straight rename is enough - unlike QuestRewardFieldNamesCorrected,
    /// which needed an ordered three-way rotation.
    /// </remarks>
    public partial class DropSnapshotPrefixOnQuestFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SnapshotXpReward",
                table: "QuestDefinitions",
                newName: "XpReward");

            migrationBuilder.RenameColumn(
                name: "SnapshotMoneyReward",
                table: "QuestDefinitions",
                newName: "MoneyReward");

            migrationBuilder.RenameColumn(
                name: "SnapshotQuestLineFlags",
                table: "QuestDefinitions",
                newName: "QuestLineFlags");

            migrationBuilder.RenameColumn(
                name: "SnapshotChainHeadQuestId",
                table: "QuestDefinitions",
                newName: "ChainHeadQuestId");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknown118",
                table: "QuestDefinitions",
                newName: "Field118");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknownEc",
                table: "QuestDefinitions",
                newName: "FieldEc");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknownF0",
                table: "QuestDefinitions",
                newName: "FieldF0");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknownF8",
                table: "QuestDefinitions",
                newName: "FieldF8");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FieldF8",
                table: "QuestDefinitions",
                newName: "SnapshotUnknownF8");

            migrationBuilder.RenameColumn(
                name: "FieldF0",
                table: "QuestDefinitions",
                newName: "SnapshotUnknownF0");

            migrationBuilder.RenameColumn(
                name: "FieldEc",
                table: "QuestDefinitions",
                newName: "SnapshotUnknownEc");

            migrationBuilder.RenameColumn(
                name: "Field118",
                table: "QuestDefinitions",
                newName: "SnapshotUnknown118");

            migrationBuilder.RenameColumn(
                name: "ChainHeadQuestId",
                table: "QuestDefinitions",
                newName: "SnapshotChainHeadQuestId");

            migrationBuilder.RenameColumn(
                name: "QuestLineFlags",
                table: "QuestDefinitions",
                newName: "SnapshotQuestLineFlags");

            migrationBuilder.RenameColumn(
                name: "MoneyReward",
                table: "QuestDefinitions",
                newName: "SnapshotMoneyReward");

            migrationBuilder.RenameColumn(
                name: "XpReward",
                table: "QuestDefinitions",
                newName: "SnapshotXpReward");
        }
    }
}
