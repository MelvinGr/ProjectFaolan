using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpellGuiStackingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Rank",
                table: "Spells",
                newName: "GuiStackingOrder");

            migrationBuilder.RenameColumn(
                name: "LineId",
                table: "Spells",
                newName: "GuiStackingLine");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GuiStackingOrder",
                table: "Spells",
                newName: "Rank");

            migrationBuilder.RenameColumn(
                name: "GuiStackingLine",
                table: "Spells",
                newName: "LineId");
        }
    }
}
