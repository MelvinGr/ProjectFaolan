using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NpcFollowPairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NpcFollowPairs",
                columns: table => new
                {
                    FollowerTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    LeaderTemplateId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcFollowPairs", x => new { x.FollowerTemplateId, x.LeaderTemplateId });
                    table.ForeignKey(
                        name: "FK_NpcFollowPairs_NpcTemplates_FollowerTemplateId",
                        column: x => x.FollowerTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcFollowPairs_NpcTemplates_LeaderTemplateId",
                        column: x => x.LeaderTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NpcFollowPairs_LeaderTemplateId",
                table: "NpcFollowPairs",
                column: "LeaderTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NpcFollowPairs");
        }
    }
}
