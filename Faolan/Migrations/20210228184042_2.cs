using Microsoft.EntityFrameworkCore.Migrations;

namespace Faolan.Migrations
{
    public partial class _2 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                "CharacterBodies",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<uint>("INTEGER", nullable: false),
                    EyebrowScale = table.Column<float>("REAL", nullable: false),
                    CheekDepth = table.Column<float>("REAL", nullable: false),
                    CheekHeight = table.Column<float>("REAL", nullable: false),
                    CheekWidth = table.Column<float>("REAL", nullable: false),
                    ChinLength = table.Column<float>("REAL", nullable: false),
                    ChinWidth = table.Column<float>("REAL", nullable: false),
                    EarAngle = table.Column<float>("REAL", nullable: false),
                    Ears = table.Column<float>("REAL", nullable: false),
                    EyesAngle = table.Column<float>("REAL", nullable: false),
                    EyesVerticalPos = table.Column<float>("REAL", nullable: false),
                    EyesHorizontalPos = table.Column<float>("REAL", nullable: false),
                    EyesDepth = table.Column<float>("REAL", nullable: false),
                    JawWidth = table.Column<float>("REAL", nullable: false),
                    FaceLength = table.Column<float>("REAL", nullable: false),
                    MouthVerticalPos = table.Column<float>("REAL", nullable: false),
                    MouthWidth = table.Column<float>("REAL", nullable: false),
                    NoseAngle = table.Column<float>("REAL", nullable: false),
                    NoseCurve = table.Column<float>("REAL", nullable: false),
                    CrookedNose = table.Column<float>("REAL", nullable: false),
                    NoseLength = table.Column<float>("REAL", nullable: false),
                    NoseWidth = table.Column<float>("REAL", nullable: false),
                    Arm = table.Column<float>("REAL", nullable: false),
                    Chest = table.Column<float>("REAL", nullable: false),
                    Bosom = table.Column<float>("REAL", nullable: false),
                    Stomach = table.Column<float>("REAL", nullable: false),
                    Arse = table.Column<float>("REAL", nullable: false),
                    Thigh = table.Column<float>("REAL", nullable: false),
                    Leg = table.Column<float>("REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterBodies", x => x.Id);
                    table.ForeignKey(
                        "FK_CharacterBodies_Characters_CharacterId",
                        x => x.CharacterId,
                        "Characters",
                        "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                "Items",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>("TEXT", maxLength: 255, nullable: true)
                },
                constraints: table => { table.PrimaryKey("PK_Items", x => x.Id); });

            migrationBuilder.CreateTable(
                "Npcs",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>("TEXT", maxLength: 255, nullable: true),
                    Action = table.Column<uint>("INTEGER", nullable: false),
                    Level = table.Column<uint>("INTEGER", nullable: false),
                    Fraction = table.Column<uint>("INTEGER", nullable: false),
                    Health = table.Column<uint>("INTEGER", nullable: false),
                    Stamina = table.Column<uint>("INTEGER", nullable: false),
                    Mana = table.Column<uint>("INTEGER", nullable: false),
                    GetExp = table.Column<uint>("INTEGER", nullable: false),
                    Extras = table.Column<string>("TEXT", nullable: true),
                    WorldMapId = table.Column<uint>("INTEGER", nullable: true),
                    PositionX = table.Column<float>("REAL", nullable: false),
                    PositionY = table.Column<float>("REAL", nullable: false),
                    PositionZ = table.Column<float>("REAL", nullable: false),
                    RotationX = table.Column<float>("REAL", nullable: false),
                    RotationY = table.Column<float>("REAL", nullable: false),
                    RotationZ = table.Column<float>("REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Npcs", x => x.Id);
                    table.ForeignKey(
                        "FK_Npcs_WorldMaps_WorldMapId",
                        x => x.WorldMapId,
                        "WorldMaps",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                "CharacterEquipment",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<uint>("INTEGER", nullable: false),
                    NeckId = table.Column<uint>("INTEGER", nullable: true),
                    HeadId = table.Column<uint>("INTEGER", nullable: true),
                    TokensId = table.Column<uint>("INTEGER", nullable: true),
                    ChestId = table.Column<uint>("INTEGER", nullable: true),
                    MainHandLeftId = table.Column<uint>("INTEGER", nullable: true),
                    MainHandRightId = table.Column<uint>("INTEGER", nullable: true),
                    OffHandLeftId = table.Column<uint>("INTEGER", nullable: true),
                    OffHandRightId = table.Column<uint>("INTEGER", nullable: true),
                    BeltId = table.Column<uint>("INTEGER", nullable: true),
                    RingLeftId = table.Column<uint>("INTEGER", nullable: true),
                    RingRightId = table.Column<uint>("INTEGER", nullable: true),
                    CloakId = table.Column<uint>("INTEGER", nullable: true),
                    ShoulderId = table.Column<uint>("INTEGER", nullable: true),
                    BootsId = table.Column<uint>("INTEGER", nullable: true),
                    HandsId = table.Column<uint>("INTEGER", nullable: true),
                    LegsId = table.Column<uint>("INTEGER", nullable: true),
                    FirstPosBagId = table.Column<uint>("INTEGER", nullable: true),
                    WristId = table.Column<uint>("INTEGER", nullable: true),
                    Bag = table.Column<string>("TEXT", nullable: true),
                    Quest = table.Column<string>("TEXT", nullable: true),
                    Resources = table.Column<string>("TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterEquipment", x => x.Id);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Characters_CharacterId",
                        x => x.CharacterId,
                        "Characters",
                        "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_BeltId",
                        x => x.BeltId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_BootsId",
                        x => x.BootsId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_ChestId",
                        x => x.ChestId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_CloakId",
                        x => x.CloakId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_FirstPosBagId",
                        x => x.FirstPosBagId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_HandsId",
                        x => x.HandsId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_HeadId",
                        x => x.HeadId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_LegsId",
                        x => x.LegsId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_MainHandLeftId",
                        x => x.MainHandLeftId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_MainHandRightId",
                        x => x.MainHandRightId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_NeckId",
                        x => x.NeckId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_OffHandLeftId",
                        x => x.OffHandLeftId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_OffHandRightId",
                        x => x.OffHandRightId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_RingLeftId",
                        x => x.RingLeftId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_RingRightId",
                        x => x.RingRightId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_ShoulderId",
                        x => x.ShoulderId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_TokensId",
                        x => x.TokensId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_CharacterEquipment_Items_WristId",
                        x => x.WristId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                "NpcBodies",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NpcId = table.Column<uint>("INTEGER", nullable: false),
                    BodyMesh = table.Column<uint>("INTEGER", nullable: false),
                    HeadMesh = table.Column<uint>("INTEGER", nullable: false),
                    HairMesh = table.Column<uint>("INTEGER", nullable: false),
                    BeardMesh = table.Column<uint>("INTEGER", nullable: false),
                    Size = table.Column<uint>("INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcBodies", x => x.Id);
                    table.ForeignKey(
                        "FK_NpcBodies_Npcs_NpcId",
                        x => x.NpcId,
                        "Npcs",
                        "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                "NpcEquipment",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NpcId = table.Column<uint>("INTEGER", nullable: false),
                    NeckId = table.Column<uint>("INTEGER", nullable: true),
                    HeadId = table.Column<uint>("INTEGER", nullable: true),
                    TokensId = table.Column<uint>("INTEGER", nullable: true),
                    ChestId = table.Column<uint>("INTEGER", nullable: true),
                    MainHandLeftId = table.Column<uint>("INTEGER", nullable: true),
                    MainHandRightId = table.Column<uint>("INTEGER", nullable: true),
                    BeltId = table.Column<uint>("INTEGER", nullable: true),
                    OffHandLeftId = table.Column<uint>("INTEGER", nullable: true),
                    OffHandRightId = table.Column<uint>("INTEGER", nullable: true),
                    RingLeftId = table.Column<uint>("INTEGER", nullable: true),
                    RingRightId = table.Column<uint>("INTEGER", nullable: true),
                    CloakId = table.Column<uint>("INTEGER", nullable: true),
                    ShoulderId = table.Column<uint>("INTEGER", nullable: true),
                    BootsId = table.Column<uint>("INTEGER", nullable: true),
                    HandsId = table.Column<uint>("INTEGER", nullable: true),
                    LegsId = table.Column<uint>("INTEGER", nullable: true),
                    WristId = table.Column<uint>("INTEGER", nullable: true),
                    FirstPosBagId = table.Column<uint>("INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcEquipment", x => x.Id);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_BeltId",
                        x => x.BeltId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_BootsId",
                        x => x.BootsId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_ChestId",
                        x => x.ChestId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_CloakId",
                        x => x.CloakId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_FirstPosBagId",
                        x => x.FirstPosBagId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_HandsId",
                        x => x.HandsId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_HeadId",
                        x => x.HeadId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_LegsId",
                        x => x.LegsId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_MainHandLeftId",
                        x => x.MainHandLeftId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_MainHandRightId",
                        x => x.MainHandRightId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_NeckId",
                        x => x.NeckId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_OffHandLeftId",
                        x => x.OffHandLeftId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_OffHandRightId",
                        x => x.OffHandRightId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_RingLeftId",
                        x => x.RingLeftId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_RingRightId",
                        x => x.RingRightId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_ShoulderId",
                        x => x.ShoulderId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_TokensId",
                        x => x.TokensId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Items_WristId",
                        x => x.WristId,
                        "Items",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        "FK_NpcEquipment_Npcs_NpcId",
                        x => x.NpcId,
                        "Npcs",
                        "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_CharacterBodies_CharacterId",
                "CharacterBodies",
                "CharacterId",
                unique: true);

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_BeltId",
                "CharacterEquipment",
                "BeltId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_BootsId",
                "CharacterEquipment",
                "BootsId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_CharacterId",
                "CharacterEquipment",
                "CharacterId",
                unique: true);

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_ChestId",
                "CharacterEquipment",
                "ChestId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_CloakId",
                "CharacterEquipment",
                "CloakId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_FirstPosBagId",
                "CharacterEquipment",
                "FirstPosBagId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_HandsId",
                "CharacterEquipment",
                "HandsId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_HeadId",
                "CharacterEquipment",
                "HeadId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_LegsId",
                "CharacterEquipment",
                "LegsId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_MainHandLeftId",
                "CharacterEquipment",
                "MainHandLeftId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_MainHandRightId",
                "CharacterEquipment",
                "MainHandRightId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_NeckId",
                "CharacterEquipment",
                "NeckId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_OffHandLeftId",
                "CharacterEquipment",
                "OffHandLeftId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_OffHandRightId",
                "CharacterEquipment",
                "OffHandRightId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_RingLeftId",
                "CharacterEquipment",
                "RingLeftId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_RingRightId",
                "CharacterEquipment",
                "RingRightId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_ShoulderId",
                "CharacterEquipment",
                "ShoulderId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_TokensId",
                "CharacterEquipment",
                "TokensId");

            migrationBuilder.CreateIndex(
                "IX_CharacterEquipment_WristId",
                "CharacterEquipment",
                "WristId");

            migrationBuilder.CreateIndex(
                "IX_NpcBodies_NpcId",
                "NpcBodies",
                "NpcId",
                unique: true);

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_BeltId",
                "NpcEquipment",
                "BeltId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_BootsId",
                "NpcEquipment",
                "BootsId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_ChestId",
                "NpcEquipment",
                "ChestId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_CloakId",
                "NpcEquipment",
                "CloakId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_FirstPosBagId",
                "NpcEquipment",
                "FirstPosBagId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_HandsId",
                "NpcEquipment",
                "HandsId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_HeadId",
                "NpcEquipment",
                "HeadId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_LegsId",
                "NpcEquipment",
                "LegsId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_MainHandLeftId",
                "NpcEquipment",
                "MainHandLeftId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_MainHandRightId",
                "NpcEquipment",
                "MainHandRightId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_NeckId",
                "NpcEquipment",
                "NeckId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_NpcId",
                "NpcEquipment",
                "NpcId",
                unique: true);

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_OffHandLeftId",
                "NpcEquipment",
                "OffHandLeftId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_OffHandRightId",
                "NpcEquipment",
                "OffHandRightId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_RingLeftId",
                "NpcEquipment",
                "RingLeftId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_RingRightId",
                "NpcEquipment",
                "RingRightId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_ShoulderId",
                "NpcEquipment",
                "ShoulderId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_TokensId",
                "NpcEquipment",
                "TokensId");

            migrationBuilder.CreateIndex(
                "IX_NpcEquipment_WristId",
                "NpcEquipment",
                "WristId");

            migrationBuilder.CreateIndex(
                "IX_Npcs_WorldMapId",
                "Npcs",
                "WorldMapId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                "CharacterBodies");

            migrationBuilder.DropTable(
                "CharacterEquipment");

            migrationBuilder.DropTable(
                "NpcBodies");

            migrationBuilder.DropTable(
                "NpcEquipment");

            migrationBuilder.DropTable(
                "Items");

            migrationBuilder.DropTable(
                "Npcs");
        }
    }
}