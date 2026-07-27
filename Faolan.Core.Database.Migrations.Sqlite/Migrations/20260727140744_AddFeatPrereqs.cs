using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatPrereqs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatPrereqs",
                columns: table => new
                {
                    WireFeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiresWireFeatId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatPrereqs", x => new { x.WireFeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatPrereqs_FeatIdMaps_WireFeatId",
                        column: x => x.WireFeatId,
                        principalTable: "FeatIdMaps",
                        principalColumn: "WireFeatId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatPrereqs");
        }
    }
}
