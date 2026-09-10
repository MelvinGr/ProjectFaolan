using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RealmLanguageAddsPolishRussianKorean : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Realm_Language",
                table: "Realms");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Realm_Language",
                table: "Realms",
                sql: "Language IN (0, 1, 2, 3, 4, 5, 6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Realm_Language",
                table: "Realms");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Realm_Language",
                table: "Realms",
                sql: "Language IN (0, 1, 2, 3)");
        }
    }
}
