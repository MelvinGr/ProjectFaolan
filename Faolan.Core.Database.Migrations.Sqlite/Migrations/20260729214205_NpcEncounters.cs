using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NpcEncounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NpcEncounters",
                columns: table => new
                {
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    LureNpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    BossNpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    BarkRadius = table.Column<float>(type: "REAL", nullable: false),
                    TriggerRadius = table.Column<float>(type: "REAL", nullable: false),
                    ResetSeconds = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcEncounters", x => new { x.MapId, x.LureNpcTemplateId });
                    table.ForeignKey(
                        name: "FK_NpcEncounters_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcEncounters_NpcTemplates_BossNpcTemplateId",
                        column: x => x.BossNpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcEncounters_NpcTemplates_LureNpcTemplateId",
                        column: x => x.LureNpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NpcEncounters_BossNpcTemplateId",
                table: "NpcEncounters",
                column: "BossNpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcEncounters_LureNpcTemplateId",
                table: "NpcEncounters",
                column: "LureNpcTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NpcEncounters");
        }
    }
}
