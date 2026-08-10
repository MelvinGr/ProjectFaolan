using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSpellStatEffectCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpellStatEffectCriteria",
                columns: table => new
                {
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pos = table.Column<uint>(type: "INTEGER", nullable: false),
                    CriterionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<uint>(type: "INTEGER", nullable: false),
                    Operator = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellStatEffectCriteria", x => new { x.SpellId, x.Sequence, x.Pos });
                    table.ForeignKey(
                        name: "FK_SpellStatEffectCriteria_SpellStatEffects_SpellId_Sequence",
                        columns: x => new { x.SpellId, x.Sequence },
                        principalTable: "SpellStatEffects",
                        principalColumns: new[] { "SpellId", "Sequence" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpellStatEffectCriteria");
        }
    }
}
