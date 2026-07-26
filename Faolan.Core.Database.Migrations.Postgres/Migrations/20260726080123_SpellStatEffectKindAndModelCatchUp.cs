using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Faolan.Core.Database.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SpellStatEffectKindAndModelCatchUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodes_PatrolNodes_ParentId",
                table: "PatrolNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodes_PatrolPaths_PatrolPathId",
                table: "PatrolNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodeWaypoints_PatrolNodes_PatrolNodeId",
                table: "PatrolNodeWaypoints");

            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodeWords_PatrolNodes_PatrolNodeId",
                table: "PatrolNodeWords");

            migrationBuilder.DropForeignKey(
                name: "FK_ShopItems_Shops_ShopId",
                table: "ShopItems");

            migrationBuilder.DropTable(
                name: "PatrolPaths");

            migrationBuilder.DropIndex(
                name: "IX_WorldObjects_MapId_PositionX_PositionY_PositionZ_DisplayName",
                table: "WorldObjects");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Shops",
                table: "Shops");

            migrationBuilder.DropIndex(
                name: "IX_Shops_NpcTemplateId",
                table: "Shops");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PatrolNodeWords",
                table: "PatrolNodeWords");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PatrolNodeWaypoints",
                table: "PatrolNodeWaypoints");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PatrolNodes",
                table: "PatrolNodes");

            migrationBuilder.DropIndex(
                name: "IX_PatrolNodes_ParentId",
                table: "PatrolNodes");

            migrationBuilder.DropIndex(
                name: "IX_PatrolNodes_PatrolPathId",
                table: "PatrolNodes");

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
                name: "PK_NpcChatter",
                table: "NpcChatter");

            migrationBuilder.DropIndex(
                name: "IX_NpcChatter_NpcTemplateId",
                table: "NpcChatter");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices");

            migrationBuilder.DropColumn(
                name: "CapturedHandle",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "CapturedHandle",
                table: "NpcSpawns");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NpcMorphs");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NpcEquipment");

            migrationBuilder.DropColumn(
                name: "RemoteFormat",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "InnerRaw",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AppearanceChoices");

            migrationBuilder.RenameColumn(
                name: "PatrolNodeId",
                table: "PatrolNodeWords",
                newName: "NpcSpawnId");

            migrationBuilder.RenameColumn(
                name: "PatrolNodeId",
                table: "PatrolNodeWaypoints",
                newName: "NpcSpawnId");

            migrationBuilder.RenameColumn(
                name: "PatrolPathId",
                table: "PatrolNodes",
                newName: "NpcSpawnId");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "NpcChatter",
                newName: "Knubot");

            migrationBuilder.AddColumn<long>(
                name: "DisplayCategory",
                table: "WorldObjects",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayFlags",
                table: "WorldObjects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DisplayId",
                table: "WorldObjects",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayKey",
                table: "WorldObjects",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "DisplayKind",
                table: "WorldObjects",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DisplayKnubot",
                table: "WorldObjects",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayToken",
                table: "WorldObjects",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowPopupOnUse",
                table: "WorldObjects",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EffectKind",
                table: "SpellStatEffects",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "OrdinalPath",
                table: "PatrolNodeWords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OrdinalPath",
                table: "PatrolNodeWaypoints",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OrdinalPath",
                table: "PatrolNodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ParentPath",
                table: "PatrolNodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Knubot",
                table: "NpcChatter",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "NpcChatter",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SayInChat",
                table: "NpcChatter",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Category",
                table: "NpcChatter",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ChannelCount",
                table: "NpcChatter",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "FormatKey",
                table: "NpcChatter",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "NpcChatter",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ParameterName",
                table: "NpcChatter",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Items",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Category",
                table: "ConversationScreenParameters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentKey",
                table: "ConversationScreenParameters",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Flags",
                table: "ConversationScreenParameters",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "ConversationScreenParameters",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<long>(
                name: "LocalizedId",
                table: "ConversationScreenParameters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalizedToken",
                table: "ConversationScreenParameters",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "ConversationScreenParameters",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Shops",
                table: "Shops",
                column: "NpcTemplateId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PatrolNodeWords",
                table: "PatrolNodeWords",
                columns: new[] { "NpcSpawnId", "OrdinalPath", "Ordinal" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_PatrolNodeWaypoints",
                table: "PatrolNodeWaypoints",
                columns: new[] { "NpcSpawnId", "OrdinalPath", "Ordinal" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_PatrolNodes",
                table: "PatrolNodes",
                columns: new[] { "NpcSpawnId", "OrdinalPath" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcMorphs",
                table: "NpcMorphs",
                columns: new[] { "NpcTemplateId", "Ordinal" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcEquipment",
                table: "NpcEquipment",
                columns: new[] { "NpcTemplateId", "Pos" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcChatter",
                table: "NpcChatter",
                columns: new[] { "NpcTemplateId", "Channel", "SayInChat" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices",
                columns: new[] { "Race", "Sex", "Category", "Sequence" });

            migrationBuilder.CreateTable(
                name: "CharacterFriends",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    FriendCharacterId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterFriends", x => new { x.CharacterId, x.FriendCharacterId });
                    table.ForeignKey(
                        name: "FK_CharacterFriends_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterFriends_Characters_FriendCharacterId",
                        column: x => x.FriendCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorldObjects_MapId",
                table: "WorldObjects",
                column: "MapId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorldObject_DisplayKind",
                table: "WorldObjects",
                sql: "\"DisplayKind\" IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolNodes_NpcSpawnId_ParentPath",
                table: "PatrolNodes",
                columns: new[] { "NpcSpawnId", "ParentPath" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConversationScreenParameter_Kind",
                table: "ConversationScreenParameters",
                sql: "\"Kind\" IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterFriends_FriendCharacterId",
                table: "CharacterFriends",
                column: "FriendCharacterId");

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodes_NpcSpawns_NpcSpawnId",
                table: "PatrolNodes",
                column: "NpcSpawnId",
                principalTable: "NpcSpawns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodes_PatrolNodes_NpcSpawnId_ParentPath",
                table: "PatrolNodes",
                columns: new[] { "NpcSpawnId", "ParentPath" },
                principalTable: "PatrolNodes",
                principalColumns: new[] { "NpcSpawnId", "OrdinalPath" });

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodeWaypoints_PatrolNodes_NpcSpawnId_OrdinalPath",
                table: "PatrolNodeWaypoints",
                columns: new[] { "NpcSpawnId", "OrdinalPath" },
                principalTable: "PatrolNodes",
                principalColumns: new[] { "NpcSpawnId", "OrdinalPath" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodeWords_PatrolNodes_NpcSpawnId_OrdinalPath",
                table: "PatrolNodeWords",
                columns: new[] { "NpcSpawnId", "OrdinalPath" },
                principalTable: "PatrolNodes",
                principalColumns: new[] { "NpcSpawnId", "OrdinalPath" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ShopItems_Shops_ShopId",
                table: "ShopItems",
                column: "ShopId",
                principalTable: "Shops",
                principalColumn: "NpcTemplateId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodes_NpcSpawns_NpcSpawnId",
                table: "PatrolNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodes_PatrolNodes_NpcSpawnId_ParentPath",
                table: "PatrolNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodeWaypoints_PatrolNodes_NpcSpawnId_OrdinalPath",
                table: "PatrolNodeWaypoints");

            migrationBuilder.DropForeignKey(
                name: "FK_PatrolNodeWords_PatrolNodes_NpcSpawnId_OrdinalPath",
                table: "PatrolNodeWords");

            migrationBuilder.DropForeignKey(
                name: "FK_ShopItems_Shops_ShopId",
                table: "ShopItems");

            migrationBuilder.DropTable(
                name: "CharacterFriends");

            migrationBuilder.DropIndex(
                name: "IX_WorldObjects_MapId",
                table: "WorldObjects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorldObject_DisplayKind",
                table: "WorldObjects");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Shops",
                table: "Shops");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PatrolNodeWords",
                table: "PatrolNodeWords");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PatrolNodeWaypoints",
                table: "PatrolNodeWaypoints");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PatrolNodes",
                table: "PatrolNodes");

            migrationBuilder.DropIndex(
                name: "IX_PatrolNodes_NpcSpawnId_ParentPath",
                table: "PatrolNodes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcMorphs",
                table: "NpcMorphs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcEquipment",
                table: "NpcEquipment");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NpcChatter",
                table: "NpcChatter");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConversationScreenParameter_Kind",
                table: "ConversationScreenParameters");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices");

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

            migrationBuilder.DropColumn(
                name: "ShowPopupOnUse",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "EffectKind",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "OrdinalPath",
                table: "PatrolNodeWords");

            migrationBuilder.DropColumn(
                name: "OrdinalPath",
                table: "PatrolNodeWaypoints");

            migrationBuilder.DropColumn(
                name: "OrdinalPath",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "ParentPath",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "SayInChat",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "ChannelCount",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "FormatKey",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "ParameterName",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "ContentKey",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "Flags",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "LocalizedId",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "LocalizedToken",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "Text",
                table: "ConversationScreenParameters");

            migrationBuilder.RenameColumn(
                name: "NpcSpawnId",
                table: "PatrolNodeWords",
                newName: "PatrolNodeId");

            migrationBuilder.RenameColumn(
                name: "NpcSpawnId",
                table: "PatrolNodeWaypoints",
                newName: "PatrolNodeId");

            migrationBuilder.RenameColumn(
                name: "NpcSpawnId",
                table: "PatrolNodes",
                newName: "PatrolPathId");

            migrationBuilder.RenameColumn(
                name: "Knubot",
                table: "NpcChatter",
                newName: "Id");

            migrationBuilder.AddColumn<long>(
                name: "CapturedHandle",
                table: "WorldObjects",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "WorldObjects",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "Shops",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "PatrolNodes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "ParentId",
                table: "PatrolNodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CapturedHandle",
                table: "NpcSpawns",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "NpcMorphs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "NpcEquipment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "NpcChatter",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<string>(
                name: "RemoteFormat",
                table: "NpcChatter",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "InnerRaw",
                table: "ConversationScreenParameters",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "AppearanceChoices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Shops",
                table: "Shops",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PatrolNodeWords",
                table: "PatrolNodeWords",
                columns: new[] { "PatrolNodeId", "Ordinal" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_PatrolNodeWaypoints",
                table: "PatrolNodeWaypoints",
                columns: new[] { "PatrolNodeId", "Ordinal" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_PatrolNodes",
                table: "PatrolNodes",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcMorphs",
                table: "NpcMorphs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcEquipment",
                table: "NpcEquipment",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NpcChatter",
                table: "NpcChatter",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppearanceChoices",
                table: "AppearanceChoices",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "PatrolPaths",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NpcSpawnId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolPaths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolPaths_NpcSpawns_NpcSpawnId",
                        column: x => x.NpcSpawnId,
                        principalTable: "NpcSpawns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorldObjects_MapId_PositionX_PositionY_PositionZ_DisplayName",
                table: "WorldObjects",
                columns: new[] { "MapId", "PositionX", "PositionY", "PositionZ", "DisplayName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shops_NpcTemplateId",
                table: "Shops",
                column: "NpcTemplateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatrolNodes_ParentId",
                table: "PatrolNodes",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolNodes_PatrolPathId",
                table: "PatrolNodes",
                column: "PatrolPathId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcMorphs_NpcTemplateId",
                table: "NpcMorphs",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcEquipment_NpcTemplateId",
                table: "NpcEquipment",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcChatter_NpcTemplateId",
                table: "NpcChatter",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPaths_NpcSpawnId",
                table: "PatrolPaths",
                column: "NpcSpawnId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodes_PatrolNodes_ParentId",
                table: "PatrolNodes",
                column: "ParentId",
                principalTable: "PatrolNodes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodes_PatrolPaths_PatrolPathId",
                table: "PatrolNodes",
                column: "PatrolPathId",
                principalTable: "PatrolPaths",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodeWaypoints_PatrolNodes_PatrolNodeId",
                table: "PatrolNodeWaypoints",
                column: "PatrolNodeId",
                principalTable: "PatrolNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PatrolNodeWords_PatrolNodes_PatrolNodeId",
                table: "PatrolNodeWords",
                column: "PatrolNodeId",
                principalTable: "PatrolNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ShopItems_Shops_ShopId",
                table: "ShopItems",
                column: "ShopId",
                principalTable: "Shops",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
