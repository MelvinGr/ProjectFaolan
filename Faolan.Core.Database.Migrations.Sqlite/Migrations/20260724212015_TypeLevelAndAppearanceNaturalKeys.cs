using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class TypeLevelAndAppearanceNaturalKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcMorphs",
                table: "NpcMorphs");

            migrationBuilder.DropIndex(
                name: "IX_NpcMorphs_NpcTemplateId",
                table: "NpcMorphs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcEquipment",
                table: "NpcEquipment");

            migrationBuilder.DropIndex(
                name: "IX_NpcEquipment_NpcTemplateId",
                table: "NpcEquipment");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NpcMorphs");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NpcEquipment");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AppearanceChoices");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcMorphs",
                table: "NpcMorphs",
                columns: new[] { "NpcTemplateId", "Ordinal" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcEquipment",
                table: "NpcEquipment",
                columns: new[] { "NpcTemplateId", "Pos" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices",
                columns: new[] { "Race", "Sex", "Category", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcMorphs",
                table: "NpcMorphs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcEquipment",
                table: "NpcEquipment");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices");

            migrationBuilder.AddColumn<uint>(
                name: "Id",
                table: "NpcMorphs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<uint>(
                name: "Id",
                table: "NpcEquipment",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<uint>(
                name: "Id",
                table: "AppearanceChoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcMorphs",
                table: "NpcMorphs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcEquipment",
                table: "NpcEquipment",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_NpcMorphs_NpcTemplateId",
                table: "NpcMorphs",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcEquipment_NpcTemplateId",
                table: "NpcEquipment",
                column: "NpcTemplateId");
        }
    }
}
