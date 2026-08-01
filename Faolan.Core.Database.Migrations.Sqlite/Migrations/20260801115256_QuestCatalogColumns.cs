using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestCatalogColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestDefinitions_Maps_MapId",
                table: "QuestDefinitions");

            migrationBuilder.AddColumn<uint>(
                name: "FirstQuestId",
                table: "QuestLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "LastQuestId",
                table: "QuestLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "QuestLines",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "PrevQuestLineId",
                table: "QuestLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "Tier",
                table: "QuestLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "QuestGoalDefinitions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "Phase",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AlterColumn<uint>(
                name: "MapId",
                table: "QuestDefinitions",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "QuestDefinitions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuestLineRewards",
                columns: table => new
                {
                    QuestLineId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pos = table.Column<uint>(type: "INTEGER", nullable: false),
                    ItemTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RewardType = table.Column<uint>(type: "INTEGER", nullable: false),
                    Optional = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestLineRewards", x => new { x.QuestLineId, x.Pos });
                    table.ForeignKey(
                        name: "FK_QuestLineRewards_QuestLines_QuestLineId",
                        column: x => x.QuestLineId,
                        principalTable: "QuestLines",
                        principalColumn: "QuestLineId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_QuestDefinitions_Maps_MapId",
                table: "QuestDefinitions",
                column: "MapId",
                principalTable: "Maps",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestDefinitions_Maps_MapId",
                table: "QuestDefinitions");

            migrationBuilder.DropTable(
                name: "QuestLineRewards");

            migrationBuilder.DropColumn(
                name: "FirstQuestId",
                table: "QuestLines");

            migrationBuilder.DropColumn(
                name: "LastQuestId",
                table: "QuestLines");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "QuestLines");

            migrationBuilder.DropColumn(
                name: "PrevQuestLineId",
                table: "QuestLines");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "QuestLines");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "QuestDefinitions");

            migrationBuilder.AlterColumn<uint>(
                name: "MapId",
                table: "QuestDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u,
                oldClrType: typeof(uint),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_QuestDefinitions_Maps_MapId",
                table: "QuestDefinitions",
                column: "MapId",
                principalTable: "Maps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
