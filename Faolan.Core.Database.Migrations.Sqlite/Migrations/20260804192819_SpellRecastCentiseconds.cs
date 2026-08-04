using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpellRecastCentiseconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Cooldown",
                table: "Spells",
                newName: "RecastCentiseconds");

            // The column changed UNIT, not just name (#271): it held seconds, it now holds the centiseconds the
            // client itself applies (value * 0.01 s). The rows already in it are the ellatha combo recasts in
            // seconds, so without this step every combo would come out 100x too short.
            migrationBuilder.Sql("UPDATE \"Spells\" SET \"RecastCentiseconds\" = \"RecastCentiseconds\" * 100;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Spells\" SET \"RecastCentiseconds\" = \"RecastCentiseconds\" / 100;");

            migrationBuilder.RenameColumn(
                name: "RecastCentiseconds",
                table: "Spells",
                newName: "Cooldown");
        }
    }
}
