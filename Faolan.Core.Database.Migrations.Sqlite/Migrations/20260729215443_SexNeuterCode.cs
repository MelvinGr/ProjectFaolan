using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SexNeuterCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_NpcStatCore_Sex",
                table: "NpcStatCores");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Character_Sex",
                table: "Characters");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppearanceSexOption_Sex",
                table: "AppearanceSexOptions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppearanceChoice_Sex",
                table: "AppearanceChoices");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NpcStatCore_Sex",
                table: "NpcStatCores",
                sql: "Sex IN (0, 1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Character_Sex",
                table: "Characters",
                sql: "Sex IN (0, 1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppearanceSexOption_Sex",
                table: "AppearanceSexOptions",
                sql: "Sex IN (0, 1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppearanceChoice_Sex",
                table: "AppearanceChoices",
                sql: "Sex IN (0, 1, 2, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_NpcStatCore_Sex",
                table: "NpcStatCores");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Character_Sex",
                table: "Characters");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppearanceSexOption_Sex",
                table: "AppearanceSexOptions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppearanceChoice_Sex",
                table: "AppearanceChoices");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NpcStatCore_Sex",
                table: "NpcStatCores",
                sql: "Sex IN (0, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Character_Sex",
                table: "Characters",
                sql: "Sex IN (0, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppearanceSexOption_Sex",
                table: "AppearanceSexOptions",
                sql: "Sex IN (0, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppearanceChoice_Sex",
                table: "AppearanceChoices",
                sql: "Sex IN (0, 2, 3)");
        }
    }
}
