using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ConversationGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #642: the dialogue tree is re-keyed from the answer-index PATH to the screen's CONTENT, and the travel
            // rows from (NpcName, AnswerPath) to the dialogue EDGE they are. Neither new key is computable from the old
            // columns - the content key comes from the child screen rows and the edge from the fold over every
            // harvested walk - so the data cannot be carried across and is CLEARED rather than defaulted.
            //
            // That is safe because both tables are fully derived from the captures: `--mode conversations=overwrite`
            // plus `travel-wiring` rebuild them whole. Defaulting instead would be worse than clearing - every
            // TravelDestination would take FromNodeId 0 and collide on the new composite primary key.
            migrationBuilder.Sql("DELETE FROM TravelDestinations");
            migrationBuilder.Sql("DELETE FROM ConversationScreenParameters");
            migrationBuilder.Sql("DELETE FROM ConversationScreens");
            migrationBuilder.Sql("DELETE FROM ConversationPackets");
            migrationBuilder.Sql("DELETE FROM ConversationNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_ConversationNodes_QuestDefinitions_GrantQuestId",
                table: "ConversationNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_ConversationNodes_WorldObjects_TriggerWorldObjectId",
                table: "ConversationNodes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations");

            migrationBuilder.DropIndex(
                name: "IX_ConversationNodes_GrantQuestId",
                table: "ConversationNodes");

            migrationBuilder.DropIndex(
                name: "IX_ConversationNodes_NpcName_TypeId_AnswerPath",
                table: "ConversationNodes");

            migrationBuilder.DropIndex(
                name: "IX_ConversationNodes_TriggerWorldObjectId",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "AnswerPath",
                table: "TravelDestinations");

            migrationBuilder.DropColumn(
                name: "AnswerPath",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "ConditionQuestId",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "ConditionQuestState",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "GrantQuestId",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "ServerInitiated",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "TriggerWorldObjectId",
                table: "ConversationNodes");

            migrationBuilder.RenameColumn(
                name: "TypeId",
                table: "ConversationNodes",
                newName: "Discriminator");

            migrationBuilder.AddColumn<uint>(
                name: "FromNodeId",
                table: "TravelDestinations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "OptionId",
                table: "TravelDestinations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "ContentKey",
                table: "ConversationNodes",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations",
                columns: new[] { "FromNodeId", "OptionId" });

            migrationBuilder.CreateTable(
                name: "ConversationEdges",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    FromNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    OptionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ToNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ConditionQuestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ConditionQuestState = table.Column<int>(type: "INTEGER", nullable: true),
                    GrantQuestId = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationEdges", x => x.Id);
                    table.CheckConstraint("CK_ConversationEdge_ConditionQuestState", "ConditionQuestState IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_ConversationEdges_ConversationNodes_FromNodeId",
                        column: x => x.FromNodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationEdges_ConversationNodes_ToNodeId",
                        column: x => x.ToNodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationEdges_QuestDefinitions_GrantQuestId",
                        column: x => x.GrantQuestId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ConversationEntries",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggerWorldObjectId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ConditionQuestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ConditionQuestState = table.Column<int>(type: "INTEGER", nullable: true),
                    NodeId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationEntries", x => x.Id);
                    table.CheckConstraint("CK_ConversationEntry_ConditionQuestState", "ConditionQuestState IN (0, 1)");
                    table.CheckConstraint("CK_ConversationEntry_Trigger", "Trigger IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_ConversationEntries_ConversationNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationEntries_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationEntries_WorldObjects_TriggerWorldObjectId",
                        column: x => x.TriggerWorldObjectId,
                        principalTable: "WorldObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_NpcName",
                table: "ConversationNodes",
                column: "NpcName");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEdges_FromNodeId",
                table: "ConversationEdges",
                column: "FromNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEdges_GrantQuestId",
                table: "ConversationEdges",
                column: "GrantQuestId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEdges_ToNodeId",
                table: "ConversationEdges",
                column: "ToNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_NodeId",
                table: "ConversationEntries",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_NpcName",
                table: "ConversationEntries",
                column: "NpcName");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_NpcTemplateId",
                table: "ConversationEntries",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_TriggerWorldObjectId",
                table: "ConversationEntries",
                column: "TriggerWorldObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_TravelDestinations_ConversationNodes_FromNodeId",
                table: "TravelDestinations",
                column: "FromNodeId",
                principalTable: "ConversationNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TravelDestinations_ConversationNodes_FromNodeId",
                table: "TravelDestinations");

            migrationBuilder.DropTable(
                name: "ConversationEdges");

            migrationBuilder.DropTable(
                name: "ConversationEntries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations");

            migrationBuilder.DropIndex(
                name: "IX_ConversationNodes_NpcName",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "FromNodeId",
                table: "TravelDestinations");

            migrationBuilder.DropColumn(
                name: "OptionId",
                table: "TravelDestinations");

            migrationBuilder.DropColumn(
                name: "ContentKey",
                table: "ConversationNodes");

            migrationBuilder.RenameColumn(
                name: "Discriminator",
                table: "ConversationNodes",
                newName: "TypeId");

            migrationBuilder.AddColumn<string>(
                name: "AnswerPath",
                table: "TravelDestinations",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AnswerPath",
                table: "ConversationNodes",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<uint>(
                name: "ConditionQuestId",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConditionQuestState",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "GrantQuestId",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ServerInitiated",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<uint>(
                name: "TriggerWorldObjectId",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_TravelDestinations",
                table: "TravelDestinations",
                columns: new[] { "NpcName", "AnswerPath" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_GrantQuestId",
                table: "ConversationNodes",
                column: "GrantQuestId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_NpcName_TypeId_AnswerPath",
                table: "ConversationNodes",
                columns: new[] { "NpcName", "TypeId", "AnswerPath" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_TriggerWorldObjectId",
                table: "ConversationNodes",
                column: "TriggerWorldObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationNodes_QuestDefinitions_GrantQuestId",
                table: "ConversationNodes",
                column: "GrantQuestId",
                principalTable: "QuestDefinitions",
                principalColumn: "QuestId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationNodes_WorldObjects_TriggerWorldObjectId",
                table: "ConversationNodes",
                column: "TriggerWorldObjectId",
                principalTable: "WorldObjects",
                principalColumn: "Id");
        }
    }
}
