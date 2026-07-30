using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMapRegions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "DiscoveryRegionId",
                table: "RespawnPoints",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "RegionId",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MapRegions",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MinY = table.Column<float>(type: "REAL", nullable: false),
                    MaxY = table.Column<float>(type: "REAL", nullable: false),
                    MaxX = table.Column<float>(type: "REAL", nullable: false),
                    MaxZ = table.Column<float>(type: "REAL", nullable: false),
                    MinX = table.Column<float>(type: "REAL", nullable: false),
                    MinZ = table.Column<float>(type: "REAL", nullable: false),
                    ScryId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapRegions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapRegions_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapRegionVertices",
                columns: table => new
                {
                    RegionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    X = table.Column<float>(type: "REAL", nullable: false),
                    Z = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapRegionVertices", x => new { x.RegionId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_MapRegionVertices_MapRegions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "MapRegions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RespawnPoints_DiscoveryRegionId",
                table: "RespawnPoints",
                column: "DiscoveryRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestGoalDefinitions_RegionId",
                table: "QuestGoalDefinitions",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_MapRegions_MapId_Name",
                table: "MapRegions",
                columns: new[] { "MapId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_QuestGoalDefinitions_MapRegions_RegionId",
                table: "QuestGoalDefinitions",
                column: "RegionId",
                principalTable: "MapRegions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_RespawnPoints_MapRegions_DiscoveryRegionId",
                table: "RespawnPoints",
                column: "DiscoveryRegionId",
                principalTable: "MapRegions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuestGoalDefinitions_MapRegions_RegionId",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropForeignKey(
                name: "FK_RespawnPoints_MapRegions_DiscoveryRegionId",
                table: "RespawnPoints");

            migrationBuilder.DropTable(
                name: "MapRegionVertices");

            migrationBuilder.DropTable(
                name: "MapRegions");

            migrationBuilder.DropIndex(
                name: "IX_RespawnPoints_DiscoveryRegionId",
                table: "RespawnPoints");

            migrationBuilder.DropIndex(
                name: "IX_QuestGoalDefinitions_RegionId",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "DiscoveryRegionId",
                table: "RespawnPoints");

            migrationBuilder.DropColumn(
                name: "RegionId",
                table: "QuestGoalDefinitions");
        }
    }
}
