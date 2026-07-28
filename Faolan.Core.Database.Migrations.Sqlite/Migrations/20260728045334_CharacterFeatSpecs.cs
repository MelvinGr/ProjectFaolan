using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CharacterFeatSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CharacterFeats",
                table: "CharacterFeats");

            migrationBuilder.AddColumn<int>(
                name: "SpecIndex",
                table: "CharacterFeats",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CharacterFeats",
                table: "CharacterFeats",
                columns: new[] { "CharacterId", "SpecIndex", "FeatId" });

            migrationBuilder.CreateTable(
                name: "CharacterFeatSpecs",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterFeatSpecs", x => new { x.CharacterId, x.Index });
                    table.ForeignKey(
                        name: "FK_CharacterFeatSpecs_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every character that already has feats needs the spec they implicitly belonged to, or the FK below
            // rejects them. SpecIndex defaulted to 0 above, so one unnamed spec 0 per such character is exactly the
            // pre-#474 shape - and it must be inserted BEFORE the constraint exists (EF 10 checks FKs on the table
            // rebuild, so a data step after the AddForeignKey is too late).
            migrationBuilder.Sql(
                """
                INSERT INTO "CharacterFeatSpecs" ("CharacterId", "Index", "Name")
                SELECT DISTINCT "CharacterId", 0, '' FROM "CharacterFeats";
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_CharacterFeats_CharacterFeatSpecs_CharacterId_SpecIndex",
                table: "CharacterFeats",
                columns: new[] { "CharacterId", "SpecIndex" },
                principalTable: "CharacterFeatSpecs",
                principalColumns: new[] { "CharacterId", "Index" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CharacterFeats_CharacterFeatSpecs_CharacterId_SpecIndex",
                table: "CharacterFeats");

            migrationBuilder.DropTable(
                name: "CharacterFeatSpecs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CharacterFeats",
                table: "CharacterFeats");

            migrationBuilder.DropColumn(
                name: "SpecIndex",
                table: "CharacterFeats");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CharacterFeats",
                table: "CharacterFeats",
                columns: new[] { "CharacterId", "FeatId" });
        }
    }
}
