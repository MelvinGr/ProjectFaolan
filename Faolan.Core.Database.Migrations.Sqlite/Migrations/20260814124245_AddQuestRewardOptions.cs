using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestRewardOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuestRewardOptions",
                columns: table => new
                {
                    QuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pos = table.Column<uint>(type: "INTEGER", nullable: false),
                    ItemTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Level = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestRewardOptions", x => new { x.QuestId, x.Pos });
                    table.ForeignKey(
                        name: "FK_QuestRewardOptions_Items_ItemTemplateId",
                        column: x => x.ItemTemplateId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuestRewardOptions_QuestDefinitions_QuestId",
                        column: x => x.QuestId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuestRewardOptions_ItemTemplateId",
                table: "QuestRewardOptions",
                column: "ItemTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuestRewardOptions");
        }
    }
}
