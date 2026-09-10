using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AlternateAdvancementPoolsAndTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "AaPvEXp",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "AaPvPXp",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "CharacterTokens",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pool = table.Column<byte>(type: "INTEGER", nullable: false),
                    Gained = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Spent = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterTokens", x => new { x.CharacterId, x.Pool });
                    table.CheckConstraint("CK_CharacterToken_Pool", "Pool IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 19, 30, 31, 32, 33, 34, 35, 36, 50, 60, 61, 62)");
                    table.ForeignKey(
                        name: "FK_CharacterTokens_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterTokens");

            migrationBuilder.DropColumn(
                name: "AaPvEXp",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "AaPvPXp",
                table: "Characters");
        }
    }
}
