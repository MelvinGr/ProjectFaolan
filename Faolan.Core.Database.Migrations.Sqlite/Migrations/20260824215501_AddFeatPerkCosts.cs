using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatPerkCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatPerkCosts",
                columns: table => new
                {
                    FeatClassEntryId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PointType = table.Column<byte>(type: "INTEGER", nullable: false),
                    Cost = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatPerkCosts", x => new { x.FeatClassEntryId, x.PointType });
                    table.CheckConstraint("CK_FeatPerkCost_PointType", "PointType IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 19, 30, 31, 32, 33, 34, 35, 36, 50, 60, 61, 62)");
                    table.ForeignKey(
                        name: "FK_FeatPerkCosts_FeatClassEntries_FeatClassEntryId",
                        column: x => x.FeatClassEntryId,
                        principalTable: "FeatClassEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatPerkCosts");
        }
    }
}
