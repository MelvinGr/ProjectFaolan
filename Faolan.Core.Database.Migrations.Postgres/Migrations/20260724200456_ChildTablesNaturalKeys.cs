using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Faolan.Core.Database.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ChildTablesNaturalKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ShopItems",
                table: "ShopItems");

            migrationBuilder.DropIndex(
                name: "IX_ShopItems_ShopId_Pos",
                table: "ShopItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_RecipeItems",
                table: "RecipeItems");

            migrationBuilder.DropIndex(
                name: "IX_RecipeItems_RecipeId",
                table: "RecipeItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcSpells",
                table: "NpcSpells");

            migrationBuilder.DropIndex(
                name: "IX_NpcSpells_NpcTemplateId",
                table: "NpcSpells");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcSpawnMorphs",
                table: "NpcSpawnMorphs");

            migrationBuilder.DropIndex(
                name: "IX_NpcSpawnMorphs_NpcSpawnId",
                table: "NpcSpawnMorphs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcSpawnEquipment",
                table: "NpcSpawnEquipment");

            migrationBuilder.DropIndex(
                name: "IX_NpcSpawnEquipment_NpcSpawnId",
                table: "NpcSpawnEquipment");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MapLootEntries",
                table: "MapLootEntries");

            migrationBuilder.DropIndex(
                name: "IX_MapLootEntries_MapId",
                table: "MapLootEntries");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "ShopItems");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "RecipeItems");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NpcSpells");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NpcSpawnMorphs");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NpcSpawnEquipment");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "MapLootEntries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ShopItems",
                table: "ShopItems",
                columns: new[] { "ShopId", "Pos" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_RecipeItems",
                table: "RecipeItems",
                columns: new[] { "RecipeId", "Role", "Position" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcSpells",
                table: "NpcSpells",
                columns: new[] { "NpcTemplateId", "SpellId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcSpawnMorphs",
                table: "NpcSpawnMorphs",
                columns: new[] { "NpcSpawnId", "Ordinal" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcSpawnEquipment",
                table: "NpcSpawnEquipment",
                columns: new[] { "NpcSpawnId", "Pos" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_MapLootEntries",
                table: "MapLootEntries",
                columns: new[] { "MapId", "ItemTemplateId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ShopItems",
                table: "ShopItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_RecipeItems",
                table: "RecipeItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcSpells",
                table: "NpcSpells");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcSpawnMorphs",
                table: "NpcSpawnMorphs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcSpawnEquipment",
                table: "NpcSpawnEquipment");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MapLootEntries",
                table: "MapLootEntries");

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "ShopItems",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "RecipeItems",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "NpcSpells",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "NpcSpawnMorphs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "NpcSpawnEquipment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "MapLootEntries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ShopItems",
                table: "ShopItems",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_RecipeItems",
                table: "RecipeItems",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcSpells",
                table: "NpcSpells",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcSpawnMorphs",
                table: "NpcSpawnMorphs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcSpawnEquipment",
                table: "NpcSpawnEquipment",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MapLootEntries",
                table: "MapLootEntries",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ShopItems_ShopId_Pos",
                table: "ShopItems",
                columns: new[] { "ShopId", "Pos" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeItems_RecipeId",
                table: "RecipeItems",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpells_NpcTemplateId",
                table: "NpcSpells",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawnMorphs_NpcSpawnId",
                table: "NpcSpawnMorphs",
                column: "NpcSpawnId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawnEquipment_NpcSpawnId",
                table: "NpcSpawnEquipment",
                column: "NpcSpawnId");

            migrationBuilder.CreateIndex(
                name: "IX_MapLootEntries_MapId",
                table: "MapLootEntries",
                column: "MapId");
        }
    }
}
