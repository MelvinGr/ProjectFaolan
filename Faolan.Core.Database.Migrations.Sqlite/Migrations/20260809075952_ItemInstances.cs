using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ItemInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "VanityShownMask",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "ItemInstances",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Container = table.Column<uint>(type: "INTEGER", nullable: false),
                    Count = table.Column<uint>(type: "INTEGER", nullable: false),
                    CreatorName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OwnerCharacterId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemInstances", x => x.Id);
                    table.CheckConstraint("CK_ItemInstance_Container", "Container IN (101, 104, 105, 111, 112, 113, 115)");
                    table.ForeignKey(
                        name: "FK_ItemInstances_Characters_OwnerCharacterId",
                        column: x => x.OwnerCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemInstances_Items_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemInstanceSockets",
                columns: table => new
                {
                    ItemInstanceId = table.Column<uint>(type: "INTEGER", nullable: false),
                    SocketIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    GemItemId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemInstanceSockets", x => new { x.ItemInstanceId, x.SocketIndex });
                    table.ForeignKey(
                        name: "FK_ItemInstanceSockets_ItemInstances_ItemInstanceId",
                        column: x => x.ItemInstanceId,
                        principalTable: "ItemInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemInstanceSockets_Items_GemItemId",
                        column: x => x.GemItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemInstances_OwnerCharacterId_Container_Slot",
                table: "ItemInstances",
                columns: new[] { "OwnerCharacterId", "Container", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemInstances_TemplateId",
                table: "ItemInstances",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemInstanceSockets_GemItemId",
                table: "ItemInstanceSockets",
                column: "GemItemId");

            // --- BACKFILL (#750). The ONE place the old shapes are read, and afterwards they are gone.
            //
            // The vanity mask is not a property of any item - it rode the wide worn row purely because that row was
            // "the character's gear" - so it moves onto the character.
            migrationBuilder.Sql(
                """
                UPDATE Characters
                SET VanityShownMask = COALESCE((SELECT e.VanityShownMask FROM CharacterEquipment e WHERE e.CharacterId = Characters.Id), 0);
                """);

            // Carried items, verbatim. An ItemId of NULL was an "empty cell" row - a cell that holds nothing is simply
            // not an item, so those do not survive the move. The Items EXISTS guard is the new FK (Restrict): the old
            // ItemId FK allowed a dangling id, this one does not.
            migrationBuilder.Sql(
                """
                INSERT INTO ItemInstances (OwnerCharacterId, Container, Slot, TemplateId, Count, CreatorName)
                SELECT ci.CharacterId, ci.Container, ci.Slot, ci.ItemId, ci.Count, ci.CreatorName
                FROM CharacterInventory ci
                WHERE ci.ItemId IS NOT NULL AND EXISTS (SELECT 1 FROM Items i WHERE i.Id = ci.ItemId);
                """);

            // The 25 worn slot COLUMNS become rows in container 101 (Equipped), each at its wire equip slot id - the
            // same mapping CharacterEquipmentSlot names and CharacterEquipment.GetSlotId used to apply in C#. Written
            // out longhand because a migration cannot reach the application model, and this is the last time anything
            // has to know the column->slot mapping at all.
            //
            // ⚠️ VanityLegsId is included, and it is the one slot the old SAVE path silently dropped:
            // SaveCharacterEquipment assigned 24 of the 25 navs and GetCharacterEquipment Included 24 of them, both
            // missing VanityLegs. The COLUMN was written by the importer, so an imported character's vanity legs are
            // in the database and are carried over here; a player who equipped a pair would have lost them.
            migrationBuilder.Sql(
                """
                INSERT INTO ItemInstances (OwnerCharacterId, Container, Slot, TemplateId, Count, CreatorName)
                SELECT CharacterId, 101, Slot, TemplateId, 1, NULL
                FROM (
                    SELECT CharacterId,    1 AS Slot, NeckId          AS TemplateId FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,    2, HeadId          FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,    3, BagId           FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,    5, ChestId         FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,    6, MainHandLeftId  FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,    7, BeltId          FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,    8, OffHandLeftId   FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,    9, RingLeftId      FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   10, RingRightId     FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   11, CloakId         FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   12, ShoulderId      FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   14, BootsId         FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   15, HandsId         FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   16, LegsId          FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   18, WristId         FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   20, MainHandRightId FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   21, OffHandRightId  FROM CharacterEquipment
                    UNION ALL SELECT CharacterId,   26, ResourceBagId   FROM CharacterEquipment
                    UNION ALL SELECT CharacterId, 1002, VanityHeadId    FROM CharacterEquipment
                    UNION ALL SELECT CharacterId, 1005, VanityChestId   FROM CharacterEquipment
                    UNION ALL SELECT CharacterId, 1007, VanityBeltId    FROM CharacterEquipment
                    UNION ALL SELECT CharacterId, 1011, VanityCapeId    FROM CharacterEquipment
                    UNION ALL SELECT CharacterId, 1014, VanityBootsId   FROM CharacterEquipment
                    UNION ALL SELECT CharacterId, 1016, VanityLegsId    FROM CharacterEquipment
                    UNION ALL SELECT CharacterId, 1018, VanityWristId   FROM CharacterEquipment
                ) worn
                WHERE TemplateId IS NOT NULL AND EXISTS (SELECT 1 FROM Items i WHERE i.Id = worn.TemplateId);
                """);

            // FirstPosBag: the legacy nav that rendered an item at bag cell 1 with NO row behind it - a second source
            // for one cell, which is why the snapshot builder had to give a real row precedence over it and the move
            // path had to "materialize" it. It becomes an ordinary bag item, and only if cell 1 is free (a real row
            // there always won). No shipped row uses it, so this is for other clones.
            migrationBuilder.Sql(
                """
                INSERT INTO ItemInstances (OwnerCharacterId, Container, Slot, TemplateId, Count, CreatorName)
                SELECT e.CharacterId, 104, 1, e.FirstPosBagId, 1, NULL
                FROM CharacterEquipment e
                WHERE e.FirstPosBagId IS NOT NULL
                  AND EXISTS (SELECT 1 FROM Items i WHERE i.Id = e.FirstPosBagId)
                  AND NOT EXISTS (SELECT 1 FROM ItemInstances ii WHERE ii.OwnerCharacterId = e.CharacterId AND ii.Container = 104 AND ii.Slot = 1);
                """);

            migrationBuilder.DropTable(
                name: "CharacterEquipment");

            migrationBuilder.DropTable(
                name: "CharacterInventory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemInstanceSockets");

            migrationBuilder.DropTable(
                name: "ItemInstances");

            migrationBuilder.DropColumn(
                name: "VanityShownMask",
                table: "Characters");

            migrationBuilder.CreateTable(
                name: "CharacterEquipment",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    BagId = table.Column<uint>(type: "INTEGER", nullable: true),
                    BeltId = table.Column<uint>(type: "INTEGER", nullable: true),
                    BootsId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ChestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    CloakId = table.Column<uint>(type: "INTEGER", nullable: true),
                    FirstPosBagId = table.Column<uint>(type: "INTEGER", nullable: true),
                    HandsId = table.Column<uint>(type: "INTEGER", nullable: true),
                    HeadId = table.Column<uint>(type: "INTEGER", nullable: true),
                    LegsId = table.Column<uint>(type: "INTEGER", nullable: true),
                    MainHandLeftId = table.Column<uint>(type: "INTEGER", nullable: true),
                    MainHandRightId = table.Column<uint>(type: "INTEGER", nullable: true),
                    NeckId = table.Column<uint>(type: "INTEGER", nullable: true),
                    OffHandLeftId = table.Column<uint>(type: "INTEGER", nullable: true),
                    OffHandRightId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ResourceBagId = table.Column<uint>(type: "INTEGER", nullable: true),
                    RingLeftId = table.Column<uint>(type: "INTEGER", nullable: true),
                    RingRightId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ShoulderId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityBeltId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityBootsId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityCapeId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityChestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityHeadId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityLegsId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityWristId = table.Column<uint>(type: "INTEGER", nullable: true),
                    WristId = table.Column<uint>(type: "INTEGER", nullable: true),
                    VanityShownMask = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterEquipment", x => x.CharacterId);
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_BagId",
                        column: x => x.BagId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_BeltId",
                        column: x => x.BeltId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_BootsId",
                        column: x => x.BootsId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_ChestId",
                        column: x => x.ChestId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_CloakId",
                        column: x => x.CloakId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_FirstPosBagId",
                        column: x => x.FirstPosBagId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_HandsId",
                        column: x => x.HandsId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_HeadId",
                        column: x => x.HeadId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_LegsId",
                        column: x => x.LegsId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_MainHandLeftId",
                        column: x => x.MainHandLeftId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_MainHandRightId",
                        column: x => x.MainHandRightId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_NeckId",
                        column: x => x.NeckId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_OffHandLeftId",
                        column: x => x.OffHandLeftId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_OffHandRightId",
                        column: x => x.OffHandRightId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_ResourceBagId",
                        column: x => x.ResourceBagId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_RingLeftId",
                        column: x => x.RingLeftId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_RingRightId",
                        column: x => x.RingRightId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_ShoulderId",
                        column: x => x.ShoulderId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_VanityBeltId",
                        column: x => x.VanityBeltId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_VanityBootsId",
                        column: x => x.VanityBootsId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_VanityCapeId",
                        column: x => x.VanityCapeId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_VanityChestId",
                        column: x => x.VanityChestId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_VanityHeadId",
                        column: x => x.VanityHeadId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_VanityLegsId",
                        column: x => x.VanityLegsId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_VanityWristId",
                        column: x => x.VanityWristId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CharacterEquipment_Items_WristId",
                        column: x => x.WristId,
                        principalTable: "Items",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CharacterInventory",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Container = table.Column<uint>(type: "INTEGER", nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Count = table.Column<uint>(type: "INTEGER", nullable: false),
                    CreatorName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterInventory", x => new { x.CharacterId, x.Container, x.Slot });
                    table.CheckConstraint("CK_CharacterInventory_Container", "Container IN (101, 104, 105, 111, 112, 113, 115)");
                    table.ForeignKey(
                        name: "FK_CharacterInventory_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterInventory_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_BagId",
                table: "CharacterEquipment",
                column: "BagId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_BeltId",
                table: "CharacterEquipment",
                column: "BeltId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_BootsId",
                table: "CharacterEquipment",
                column: "BootsId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_ChestId",
                table: "CharacterEquipment",
                column: "ChestId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_CloakId",
                table: "CharacterEquipment",
                column: "CloakId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_FirstPosBagId",
                table: "CharacterEquipment",
                column: "FirstPosBagId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_HandsId",
                table: "CharacterEquipment",
                column: "HandsId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_HeadId",
                table: "CharacterEquipment",
                column: "HeadId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_LegsId",
                table: "CharacterEquipment",
                column: "LegsId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_MainHandLeftId",
                table: "CharacterEquipment",
                column: "MainHandLeftId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_MainHandRightId",
                table: "CharacterEquipment",
                column: "MainHandRightId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_NeckId",
                table: "CharacterEquipment",
                column: "NeckId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_OffHandLeftId",
                table: "CharacterEquipment",
                column: "OffHandLeftId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_OffHandRightId",
                table: "CharacterEquipment",
                column: "OffHandRightId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_ResourceBagId",
                table: "CharacterEquipment",
                column: "ResourceBagId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_RingLeftId",
                table: "CharacterEquipment",
                column: "RingLeftId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_RingRightId",
                table: "CharacterEquipment",
                column: "RingRightId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_ShoulderId",
                table: "CharacterEquipment",
                column: "ShoulderId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_VanityBeltId",
                table: "CharacterEquipment",
                column: "VanityBeltId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_VanityBootsId",
                table: "CharacterEquipment",
                column: "VanityBootsId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_VanityCapeId",
                table: "CharacterEquipment",
                column: "VanityCapeId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_VanityChestId",
                table: "CharacterEquipment",
                column: "VanityChestId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_VanityHeadId",
                table: "CharacterEquipment",
                column: "VanityHeadId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_VanityLegsId",
                table: "CharacterEquipment",
                column: "VanityLegsId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_VanityWristId",
                table: "CharacterEquipment",
                column: "VanityWristId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterEquipment_WristId",
                table: "CharacterEquipment",
                column: "WristId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterInventory_ItemId",
                table: "CharacterInventory",
                column: "ItemId");
        }
    }
}
