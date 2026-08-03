using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FeatProcsAndPerSpellHitPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<uint>(
                name: "SpellLineId",
                table: "FeatSpellModifiers",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<uint>(
                name: "SpellId",
                table: "FeatSpellModifiers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FeatProcs",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ComboId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SpellLineId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SelfSpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    TargetSpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Trigger = table.Column<uint>(type: "INTEGER", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatProcs", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatProcs_Feats_FeatId",
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
                name: "FeatProcs");

            migrationBuilder.DropColumn(
                name: "SpellId",
                table: "FeatSpellModifiers");

            migrationBuilder.AlterColumn<uint>(
                name: "SpellLineId",
                table: "FeatSpellModifiers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u,
                oldClrType: typeof(uint),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
