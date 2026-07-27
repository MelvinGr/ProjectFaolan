using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ShopContainerKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CharacterInventory_Container",
                table: "CharacterInventory");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CharacterInventory_Container",
                table: "CharacterInventory",
                sql: "Container IN (101, 104, 105, 111, 112, 113, 115)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CharacterInventory_Container",
                table: "CharacterInventory");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CharacterInventory_Container",
                table: "CharacterInventory",
                sql: "Container IN (101, 104, 105, 112, 113, 115)");
        }
    }
}
