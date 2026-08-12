using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SplitNpcAndPvpKillCounters : Migration
    {
        // ⚠️ HAND-CORRECTED. The scaffold crossed the rename - it moved `Kills`/`Deaths` into `PvpKills`/`PvpDeaths`
        // and added the NPC pair empty, which is exactly backwards for the DATA. Every value those columns hold was
        // put there by `CombatService.AwardKillXp` (a creature kill) or `WorldSimulation`'s death path (dying to a
        // mob or the world), so they are PvE tallies. The PvP pair has never been written by anything and must start
        // at 0, which is also what retail sends for a character with no PvP record. See #808.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Kills",
                table: "Characters",
                newName: "NpcKills");

            migrationBuilder.RenameColumn(
                name: "Deaths",
                table: "Characters",
                newName: "NpcDeaths");

            migrationBuilder.AddColumn<uint>(
                name: "PvpDeaths",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "PvpKills",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PvpDeaths",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "PvpKills",
                table: "Characters");

            migrationBuilder.RenameColumn(
                name: "NpcKills",
                table: "Characters",
                newName: "Kills");

            migrationBuilder.RenameColumn(
                name: "NpcDeaths",
                table: "Characters",
                newName: "Deaths");
        }
    }
}
