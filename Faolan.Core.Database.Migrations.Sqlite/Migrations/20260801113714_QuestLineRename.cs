using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// HAND-EDITED - do not re-scaffold this one and take what you get. `dotnet ef migrations add` produced two
    /// wrong things here, both silent:
    ///
    /// 1. `QuestArea` -> `QuestLine` scaffolded as DropTable + CreateTable, which would have destroyed all 973
    ///    rows. Rewritten as RenameTable + RenameColumn (the known class-rename trap).
    /// 2. ⚠️ The four `Snapshot*` column renames were MIS-PAIRED. EF matches renamed columns by type and
    ///    ordinal, not by intent, so with four same-typed columns renamed at once it emitted
    ///    `SnapshotUnknownF8 -> SnapshotXpReward`, `SnapshotUnknownF0 -> SnapshotQuestLineReward`,
    ///    `SnapshotUnknown28 -> SnapshotQuestLineFlags` and `SnapshotUnknown14 -> SnapshotCashReward` - every
    ///    one crossed, which would have shuffled the XP values into the flags column and back. It even crossed
    ///    the widths (F8 is the u64). The pairings below are the correct ones:
    ///        @+0x14 -> SnapshotQuestLineFlags    @+0x28 -> SnapshotQuestLineReward
    ///        @+0xf0 -> SnapshotXpReward          @+0xf8 -> SnapshotCashReward
    /// </remarks>
    public partial class QuestLineRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestDefinitions_QuestAreas_AreaId",
                table: "QuestDefinitions");

            // --- QuestDefinitions: the Quest_c record scalars that now have names, correctly paired ---
            migrationBuilder.RenameColumn(
                name: "SnapshotUnknown14",
                table: "QuestDefinitions",
                newName: "SnapshotQuestLineFlags");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknown28",
                table: "QuestDefinitions",
                newName: "SnapshotQuestLineReward");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknownF0",
                table: "QuestDefinitions",
                newName: "SnapshotXpReward");

            migrationBuilder.RenameColumn(
                name: "SnapshotUnknownF8",
                table: "QuestDefinitions",
                newName: "SnapshotCashReward");

            migrationBuilder.RenameColumn(
                name: "IndexAreaQuestIds",
                table: "QuestDefinitions",
                newName: "IndexQuestLineQuestIds");

            migrationBuilder.RenameColumn(
                name: "AreaId",
                table: "QuestDefinitions",
                newName: "QuestLineId");

            migrationBuilder.RenameIndex(
                name: "IX_QuestDefinitions_AreaId",
                table: "QuestDefinitions",
                newName: "IX_QuestDefinitions_QuestLineId");

            // --- QuestAreas -> QuestLines, rows preserved ---
            migrationBuilder.RenameTable(
                name: "QuestAreas",
                newName: "QuestLines");

            migrationBuilder.RenameColumn(
                name: "AreaId",
                table: "QuestLines",
                newName: "QuestLineId");

            migrationBuilder.RenameColumn(
                name: "AreaTitleCategory",
                table: "QuestLines",
                newName: "TitleCategory");

            migrationBuilder.RenameColumn(
                name: "AreaTitleRefId",
                table: "QuestLines",
                newName: "TitleRefId");

            migrationBuilder.RenameColumn(
                name: "AreaTitleKey1",
                table: "QuestLines",
                newName: "TitleKey1");

            migrationBuilder.RenameColumn(
                name: "AreaTitleKey2",
                table: "QuestLines",
                newName: "TitleKey2");

            migrationBuilder.RenameColumn(
                name: "AreaTitleKey3",
                table: "QuestLines",
                newName: "TitleKey3");

            migrationBuilder.RenameColumn(
                name: "AreaTitleKey4",
                table: "QuestLines",
                newName: "TitleKey4");

            migrationBuilder.AddForeignKey(
                name: "FK_QuestDefinitions_QuestLines_QuestLineId",
                table: "QuestDefinitions",
                column: "QuestLineId",
                principalTable: "QuestLines",
                principalColumn: "QuestLineId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestDefinitions_QuestLines_QuestLineId",
                table: "QuestDefinitions");

            migrationBuilder.RenameColumn(
                name: "TitleKey4",
                table: "QuestLines",
                newName: "AreaTitleKey4");

            migrationBuilder.RenameColumn(
                name: "TitleKey3",
                table: "QuestLines",
                newName: "AreaTitleKey3");

            migrationBuilder.RenameColumn(
                name: "TitleKey2",
                table: "QuestLines",
                newName: "AreaTitleKey2");

            migrationBuilder.RenameColumn(
                name: "TitleKey1",
                table: "QuestLines",
                newName: "AreaTitleKey1");

            migrationBuilder.RenameColumn(
                name: "TitleRefId",
                table: "QuestLines",
                newName: "AreaTitleRefId");

            migrationBuilder.RenameColumn(
                name: "TitleCategory",
                table: "QuestLines",
                newName: "AreaTitleCategory");

            migrationBuilder.RenameColumn(
                name: "QuestLineId",
                table: "QuestLines",
                newName: "AreaId");

            migrationBuilder.RenameTable(
                name: "QuestLines",
                newName: "QuestAreas");

            migrationBuilder.RenameIndex(
                name: "IX_QuestDefinitions_QuestLineId",
                table: "QuestDefinitions",
                newName: "IX_QuestDefinitions_AreaId");

            migrationBuilder.RenameColumn(
                name: "QuestLineId",
                table: "QuestDefinitions",
                newName: "AreaId");

            migrationBuilder.RenameColumn(
                name: "IndexQuestLineQuestIds",
                table: "QuestDefinitions",
                newName: "IndexAreaQuestIds");

            migrationBuilder.RenameColumn(
                name: "SnapshotCashReward",
                table: "QuestDefinitions",
                newName: "SnapshotUnknownF8");

            migrationBuilder.RenameColumn(
                name: "SnapshotXpReward",
                table: "QuestDefinitions",
                newName: "SnapshotUnknownF0");

            migrationBuilder.RenameColumn(
                name: "SnapshotQuestLineReward",
                table: "QuestDefinitions",
                newName: "SnapshotUnknown28");

            migrationBuilder.RenameColumn(
                name: "SnapshotQuestLineFlags",
                table: "QuestDefinitions",
                newName: "SnapshotUnknown14");

            migrationBuilder.AddForeignKey(
                name: "FK_QuestDefinitions_QuestAreas_AreaId",
                table: "QuestDefinitions",
                column: "AreaId",
                principalTable: "QuestAreas",
                principalColumn: "AreaId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
