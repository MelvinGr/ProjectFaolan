using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddKhitanRace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Character_Race",
                table: "Characters");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppearanceChoice_Race",
                table: "AppearanceChoices");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Character_Race",
                table: "Characters",
                sql: "Race IN (1, 2, 3, 10)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppearanceChoice_Race",
                table: "AppearanceChoices",
                sql: "Race IN (1, 2, 3, 10)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Character_Race",
                table: "Characters");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppearanceChoice_Race",
                table: "AppearanceChoices");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Character_Race",
                table: "Characters",
                sql: "Race IN (1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppearanceChoice_Race",
                table: "AppearanceChoices",
                sql: "Race IN (1, 2, 3)");
        }
    }
}
