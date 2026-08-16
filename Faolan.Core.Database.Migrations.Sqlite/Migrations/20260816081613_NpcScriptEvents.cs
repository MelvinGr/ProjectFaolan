using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NpcScriptEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NpcScriptEvents",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Event = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcScriptEvents", x => new { x.NpcTemplateId, x.Event });
                    table.ForeignKey(
                        name: "FK_NpcScriptEvents_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NpcScriptEvents");
        }
    }
}
