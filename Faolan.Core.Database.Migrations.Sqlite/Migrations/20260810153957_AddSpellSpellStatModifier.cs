using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSpellSpellStatModifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpellSpellStatModifiers",
                columns: table => new
                {
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    TargetSpellLineId = table.Column<uint>(type: "INTEGER", nullable: true),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: true),
                    PercentHundredths = table.Column<int>(type: "INTEGER", nullable: true),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellSpellStatModifiers", x => new { x.SpellId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_SpellSpellStatModifiers_Spells_SpellId",
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
                name: "SpellSpellStatModifiers");
        }
    }
}
