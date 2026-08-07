using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FeatComboAttackModifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatComboAttackModifiers",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ComboId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AttackSlot = table.Column<uint>(type: "INTEGER", nullable: false),
                    DpsPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    CriticalChanceRating = table.Column<int>(type: "INTEGER", nullable: false),
                    HatePercent = table.Column<int>(type: "INTEGER", nullable: false),
                    DamageTypeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatComboAttackModifiers", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatComboAttackModifiers_Feats_FeatId",
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
                name: "FeatComboAttackModifiers");
        }
    }
}
