using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MapHazards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_MapHazards_MapId_Name",
                table: "MapHazards",
                columns: new[] { "MapId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapHazardVertices");

            migrationBuilder.DropTable(
                name: "MapHazards");
        }
    }
}
