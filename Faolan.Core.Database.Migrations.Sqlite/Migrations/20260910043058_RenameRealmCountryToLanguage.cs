using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RenameRealmCountryToLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Realm_Country",
                table: "Realms");

            migrationBuilder.RenameColumn(
                name: "Country",
                table: "Realms",
                newName: "Language");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Realm_Language",
                table: "Realms",
                sql: "Language IN (0, 1, 2, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Realm_Language",
                table: "Realms");

            migrationBuilder.RenameColumn(
                name: "Language",
                table: "Realms",
                newName: "Country");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Realm_Country",
                table: "Realms",
                sql: "Country IN (0, 1, 2, 3)");
        }
    }
}
