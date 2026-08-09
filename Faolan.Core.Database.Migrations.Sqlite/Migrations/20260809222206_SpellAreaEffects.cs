using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpellAreaEffects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpellAreaEffects",
                columns: table => new
                {
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<uint>(type: "INTEGER", nullable: false),
                    Kind = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectSpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RadiusMetres = table.Column<uint>(type: "INTEGER", nullable: false),
                    MaxTargets = table.Column<uint>(type: "INTEGER", nullable: false),
                    PeriodCentiseconds = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellAreaEffects", x => new { x.SpellId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_SpellAreaEffects_Spells_SpellId",
                        column: x => x.SpellId,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpellAreaEffects");
        }
    }
}
