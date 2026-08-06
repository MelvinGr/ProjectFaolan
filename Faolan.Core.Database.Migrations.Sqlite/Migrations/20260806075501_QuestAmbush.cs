using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestAmbush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NpcAmbushes",
                columns: table => new
                {
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestDefinitionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    GoalId = table.Column<uint>(type: "INTEGER", nullable: false),
                    DelaySeconds = table.Column<float>(type: "REAL", nullable: false),
                    AnchorDistance = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcAmbushes", x => new { x.MapId, x.NpcTemplateId });
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_QuestDefinitions_QuestDefinitionId",
                        column: x => x.QuestDefinitionId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_QuestGoalDefinitions_GoalId",
                        column: x => x.GoalId,
                        principalTable: "QuestGoalDefinitions",
                        principalColumn: "GoalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NpcAmbushes_GoalId",
                table: "NpcAmbushes",
                column: "GoalId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcAmbushes_NpcTemplateId",
                table: "NpcAmbushes",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcAmbushes_QuestDefinitionId",
                table: "NpcAmbushes",
                column: "QuestDefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NpcAmbushes");
        }
    }
}
