using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NpcSpawnClassSpecificMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NpcSpawns_MapId_PositionX_PositionY_PositionZ",
                table: "NpcSpawns");

            migrationBuilder.AddColumn<byte>(
                name: "ClassId",
                table: "NpcSpawns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClassSpecific",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawns_MapId_PositionX_PositionY_PositionZ_ClassId",
                table: "NpcSpawns",
                columns: new[] { "MapId", "PositionX", "PositionY", "PositionZ", "ClassId" },
                unique: true);

            // The city armories: a per-class population (see NpcSpawn.ClassId), and therefore Solo - two classes cannot
            // share one zone copy of a playfield whose NPCs differ by class. Retail agrees on the instancing: the
            // IntendedTeleport into Conarch Armory carries a nonzero playfield-instance token, unlike an open-world map.
            // A no-op on a database whose Maps are still empty (a fresh install seeds them from faolan.sql afterwards,
            // and that seed carries the flags).
            migrationBuilder.Sql("UPDATE Maps SET ClassSpecific = 1, InstanceMode = 1 WHERE Id IN (5001, 5007, 5013);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping ClassSpecific takes itself with it; InstanceMode is a pre-existing column, so put it back.
            migrationBuilder.Sql("UPDATE Maps SET InstanceMode = 0 WHERE Id IN (5001, 5007, 5013);");

            migrationBuilder.DropIndex(
                name: "IX_NpcSpawns_MapId_PositionX_PositionY_PositionZ_ClassId",
                table: "NpcSpawns");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "NpcSpawns");

            migrationBuilder.DropColumn(
                name: "ClassSpecific",
                table: "Maps");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawns_MapId_PositionX_PositionY_PositionZ",
                table: "NpcSpawns",
                columns: new[] { "MapId", "PositionX", "PositionY", "PositionZ" },
                unique: true);
        }
    }
}
