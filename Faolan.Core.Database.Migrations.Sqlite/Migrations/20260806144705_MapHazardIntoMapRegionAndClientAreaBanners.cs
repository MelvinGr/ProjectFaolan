using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// #679. MapHazard stops being a table of its own and becomes the BEHAVIOUR side table of MapRegion (the two were
    /// the same 1000053 prism stored twice), and MapArea's SampleCount becomes a Source provenance enum.
    ///
    /// ⚠️ HAND-WRITTEN over what the scaffolder produced, which turned both changes into column RENAMES:
    /// `MapHazards.MapId` -> `RegionId` and `MapAreas.SampleCount` -> `Source`. Neither pair holds the same thing.
    /// A rename would have left every hazard pointing at a MapRegion id that is really a playfield id, and every
    /// banner claiming a Source of "7 sightings". The hazards are DROPPED and rebuilt from the client RDB instead
    /// (`--only rdb-regions --mode overwrite`), which is where all of that data comes from anyway; Source is added
    /// fresh, and its 0 default is `Captured`, which is exactly what every pre-existing row is.
    /// </remarks>
    public partial class MapHazardIntoMapRegionAndClientAreaBanners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapHazardVertices");

            migrationBuilder.DropTable(
                name: "MapHazards");

            migrationBuilder.CreateTable(
                name: "MapHazards",
                columns: table => new
                {
                    RegionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectSpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Damage = table.Column<uint>(type: "INTEGER", nullable: false),
                    Lethal = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapHazards", x => x.RegionId);
                    table.ForeignKey(
                        name: "FK_MapHazards_MapRegions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "MapRegions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.DropColumn(
                name: "SampleCount",
                table: "MapAreas");

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "MapAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "MapAreas");

            migrationBuilder.AddColumn<uint>(
                name: "SampleCount",
                table: "MapAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.DropTable(
                name: "MapHazards");

            migrationBuilder.CreateTable(
                name: "MapHazards",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    EffectSpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Damage = table.Column<uint>(type: "INTEGER", nullable: false),
                    Lethal = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinY = table.Column<float>(type: "REAL", nullable: false),
                    MaxY = table.Column<float>(type: "REAL", nullable: false),
                    MaxX = table.Column<float>(type: "REAL", nullable: false),
                    MaxZ = table.Column<float>(type: "REAL", nullable: false),
                    MinX = table.Column<float>(type: "REAL", nullable: false),
                    MinZ = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapHazards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapHazards_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapHazards_MapId_Name",
                table: "MapHazards",
                columns: new[] { "MapId", "Name" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "MapHazardVertices",
                columns: table => new
                {
                    HazardId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    X = table.Column<float>(type: "REAL", nullable: false),
                    Z = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapHazardVertices", x => new { x.HazardId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_MapHazardVertices_MapHazards_HazardId",
                        column: x => x.HazardId,
                        principalTable: "MapHazards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }
    }
}
