using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FeatSpellModifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "LineId",
                table: "Spells",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "FeatSpellModifiers",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SpellLineId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PercentHundredths = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatSpellModifiers", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatSpellModifiers_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatSpellModifiers");

            migrationBuilder.DropColumn(
                name: "LineId",
                table: "Spells");
        }
    }
}
