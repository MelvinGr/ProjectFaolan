using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NpcAmbientAnimations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NpcAmbientAnimations",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AnimationId = table.Column<uint>(type: "INTEGER", nullable: false),
                    DurationCentiseconds = table.Column<uint>(type: "INTEGER", nullable: false),
                    Weight = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcAmbientAnimations", x => new { x.NpcTemplateId, x.AnimationId });
                    table.ForeignKey(
                        name: "FK_NpcAmbientAnimations_NpcTemplates_NpcTemplateId",
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
                name: "NpcAmbientAnimations");
        }
    }
}
