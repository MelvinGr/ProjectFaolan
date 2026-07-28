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

            // EVERY character gets the one unnamed spec it implicitly always had - not just the ones that currently
            // have feats. Two reasons it is all of them:
            //   1. the FK below rejects any existing feat row whose spec is missing, and
            //   2. the spec is not optional. The wire's sublist always carries one, so PlayerSpawnScaffoldRows.SpecsOf
            //      THROWS on a character with none rather than synthesizing one - which means a character that has
            //      simply not trained anything yet must still have its spec row, or it could not spawn.
            // It must be inserted BEFORE the constraint exists: EF 10 checks FKs during the SQLite table rebuild, so
            // a data step after the AddForeignKey is too late.
            migrationBuilder.Sql(
                """
                INSERT INTO "CharacterFeatSpecs" ("CharacterId", "Index", "Name")
                SELECT "Id", 0, '' FROM "Characters";
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
