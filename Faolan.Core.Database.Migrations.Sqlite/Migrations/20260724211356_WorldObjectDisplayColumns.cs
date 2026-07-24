using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class WorldObjectDisplayColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorldObjects_MapId_PositionX_PositionY_PositionZ_DisplayName",
                table: "WorldObjects");

            migrationBuilder.AddColumn<uint>(
                name: "DisplayCategory",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayFlags",
                table: "WorldObjects",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "DisplayId",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayKey",
                table: "WorldObjects",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "DisplayKind",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "DisplayKnubot",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayToken",
                table: "WorldObjects",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorldObjects_MapId",
                table: "WorldObjects",
                column: "MapId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorldObject_DisplayKind",
                table: "WorldObjects",
                sql: "DisplayKind IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorldObjects_MapId",
                table: "WorldObjects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorldObject_DisplayKind",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayCategory",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayFlags",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayId",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayKey",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayKind",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayKnubot",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayToken",
                table: "WorldObjects");

            migrationBuilder.CreateIndex(
                name: "IX_WorldObjects_MapId_PositionX_PositionY_PositionZ_DisplayName",
                table: "WorldObjects",
                columns: new[] { "MapId", "PositionX", "PositionY", "PositionZ", "DisplayName" },
                unique: true);
        }
    }
}
