using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Postgres.Migrations
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
                    SpellId = table.Column<long>(type: "bigint", nullable: false),
                    Trigger = table.Column<byte>(type: "smallint", nullable: false),
                    Sequence = table.Column<byte>(type: "smallint", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    TargetSpellId = table.Column<long>(type: "bigint", nullable: true),
                    Template = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Combat = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    MaxDurationMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellEffects", x => new { x.SpellId, x.Trigger, x.Sequence });
                    table.CheckConstraint("CK_SpellEffect_Kind", "\"Kind\" IN (0, 1)");
                    table.CheckConstraint("CK_SpellEffect_Trigger", "\"Trigger\" IN (0, 1)");
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
