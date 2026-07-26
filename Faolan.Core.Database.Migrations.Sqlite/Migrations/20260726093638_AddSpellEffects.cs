using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSpellEffects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpellEffects",
                columns: table => new
                {
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<byte>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<byte>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    TargetSpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Template = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Combat = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<uint>(type: "INTEGER", nullable: true),
                    MaxDurationMs = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellEffects", x => new { x.SpellId, x.Trigger, x.Sequence });
                    table.CheckConstraint("CK_SpellEffect_Kind", "Kind IN (0, 1)");
                    table.CheckConstraint("CK_SpellEffect_Trigger", "Trigger IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_SpellEffects_Spells_SpellId",
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
                name: "SpellEffects");
        }
    }
}
