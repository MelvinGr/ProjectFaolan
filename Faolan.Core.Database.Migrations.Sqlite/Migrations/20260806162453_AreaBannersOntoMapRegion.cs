using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// #680. A banner stops being a table of its own and becomes a nullable column on the volume that fires it.
    ///
    /// `MapAreas` is DROPPED rather than migrated, and the rows are not carried across, because what it held was a
    /// (playfield, banner) pair plus a HULL over the positions retail was captured firing that banner at - and the
    /// hull was the part with no home here. The pairs come back on the next `rdb-regions` run, which harvests them
    /// from the captures and puts each on the client's own trigger volume; the hull is not stored at all any more.
    /// Two of the 142 banners reach no volume and are dropped with a note, which is the deliberate cost of the move.
    /// </remarks>
    public partial class AreaBannersOntoMapRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapAreas");

            migrationBuilder.AddColumn<uint>(
                name: "AreaNameId",
                table: "MapRegions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AreaNameSource",
                table: "MapRegions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AreaNameId",
                table: "MapRegions");

            migrationBuilder.DropColumn(
                name: "AreaNameSource",
                table: "MapRegions");

            migrationBuilder.CreateTable(
                name: "MapAreas",
                columns: table => new
                {
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AreaNameId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionId = table.Column<uint>(type: "INTEGER", nullable: true),
                    MaxX = table.Column<float>(type: "REAL", nullable: false),
                    MaxY = table.Column<float>(type: "REAL", nullable: false),
                    MaxZ = table.Column<float>(type: "REAL", nullable: false),
                    MinX = table.Column<float>(type: "REAL", nullable: false),
                    MinY = table.Column<float>(type: "REAL", nullable: false),
                    MinZ = table.Column<float>(type: "REAL", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapAreas", x => new { x.MapId, x.AreaNameId });
                    table.ForeignKey(
                        name: "FK_MapAreas_MapRegions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "MapRegions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MapAreas_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapAreas_RegionId",
                table: "MapAreas",
                column: "RegionId");
        }
    }
}
