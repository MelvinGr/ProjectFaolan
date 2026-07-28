using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Faolan.Core.Database.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Abilities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Key1 = table.Column<long>(type: "bigint", nullable: false),
                    Key2 = table.Column<long>(type: "bigint", nullable: false),
                    Key3 = table.Column<long>(type: "bigint", nullable: false),
                    Key4 = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Data0 = table.Column<long>(type: "bigint", nullable: false),
                    Slot = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Abilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    State = table.Column<byte>(type: "smallint", nullable: false),
                    Type = table.Column<byte>(type: "smallint", nullable: false),
                    CreationDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastConnection = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClaimsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    GmGodMode = table.Column<bool>(type: "boolean", nullable: false),
                    GmInvisible = table.Column<bool>(type: "boolean", nullable: false),
                    Password = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthChallenge = table.Column<Guid>(type: "uuid", nullable: false),
                    LastIpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AuthStatus = table.Column<long>(type: "bigint", nullable: false),
                    ClientInstance = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.CheckConstraint("CK_Account_State", "\"State\" IN (0, 1, 2)");
                    table.CheckConstraint("CK_Account_Type", "\"Type\" IN (0, 1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "AppearanceChoices",
                columns: table => new
                {
                    Race = table.Column<byte>(type: "smallint", nullable: false),
                    Sex = table.Column<byte>(type: "smallint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Max = table.Column<int>(type: "integer", nullable: false),
                    Min = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Color = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppearanceChoices", x => new { x.Race, x.Sex, x.Category, x.Sequence });
                    table.CheckConstraint("CK_AppearanceChoice_Race", "\"Race\" IN (1, 2, 3, 10)");
                    table.CheckConstraint("CK_AppearanceChoice_Sex", "\"Sex\" IN (0, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "AppearanceSexOptions",
                columns: table => new
                {
                    Sex = table.Column<byte>(type: "smallint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Max = table.Column<int>(type: "integer", nullable: false),
                    Min = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppearanceSexOptions", x => new { x.Sex, x.Category, x.Sequence });
                    table.CheckConstraint("CK_AppearanceSexOption_Sex", "\"Sex\" IN (0, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    SenderId = table.Column<long>(type: "bigint", nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsWhisper = table.Column<bool>(type: "boolean", nullable: false),
                    ChannelType = table.Column<byte>(type: "smallint", nullable: false),
                    SenderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClassAttributeBases",
                columns: table => new
                {
                    Class = table.Column<byte>(type: "smallint", nullable: false),
                    Level = table.Column<long>(type: "bigint", nullable: false),
                    StatId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassAttributeBases", x => new { x.Class, x.Level, x.StatId });
                    table.CheckConstraint("CK_ClassAttributeBase_Class", "\"Class\" IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                    table.CheckConstraint("CK_ClassAttributeBase_StatId", "\"StatId\" IN (0, 1, 2, 11, 12, 13, 20, 21, 23, 25, 27, 33, 34, 37, 39, 47, 51, 52, 54, 57, 58, 59, 60, 61, 70, 75, 79, 90, 91, 100, 102, 104, 106, 110, 127, 131, 135, 139, 140, 141, 143, 149, 157, 158, 162, 165, 167, 169, 170, 173, 174, 186, 187, 191, 215, 216, 217, 218, 219, 220, 222, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 238, 240, 241, 243, 254, 255, 260, 269, 271, 275, 278, 279, 280, 281, 282, 287, 291, 292, 296, 300, 301, 308, 310, 311, 312, 319, 320, 326, 330, 331, 334, 346, 350, 353, 355, 360, 363, 375, 380, 389, 448, 450, 451, 454, 456, 458, 465, 475, 476, 477, 480, 488, 490, 491, 493, 498, 504, 505, 506, 507, 536, 560, 561, 577, 594, 595, 596, 597, 607, 656, 658, 666, 667, 668, 669, 670, 671, 690, 711, 713, 804, 805, 806, 807, 808, 809, 810, 811, 812, 813, 814, 815, 816, 817, 818, 819, 821, 823, 824, 825, 827, 828, 829, 830, 831, 833, 835, 837, 840, 861, 864, 866, 867, 868, 869, 870, 871, 872, 873, 874, 875, 876, 877, 878, 879, 880, 899, 901, 902, 903, 904, 905, 906, 907, 908, 909, 920, 922, 923, 925, 926, 927, 928, 929, 931, 932, 933, 934, 935, 936, 937, 938, 939, 940, 941, 942, 943, 944, 945, 946, 948, 965, 1007, 1008, 1009, 1010, 1035, 1037, 1040, 1071, 1085, 1093, 1095, 1096, 1097, 1098, 1099, 1332, 1376, 1378, 1380, 1382, 1383, 1385, 1386, 1387, 1390, 1392, 1393, 1395, 1397, 1403)");
                });

            migrationBuilder.CreateTable(
                name: "ClassComboScalings",
                columns: table => new
                {
                    Class = table.Column<byte>(type: "smallint", nullable: false),
                    Level = table.Column<long>(type: "bigint", nullable: false),
                    DpsModifier = table.Column<long>(type: "bigint", nullable: false),
                    SecondaryModifier = table.Column<long>(type: "bigint", nullable: false),
                    StaminaDivisor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassComboScalings", x => new { x.Class, x.Level });
                    table.CheckConstraint("CK_ClassComboScaling_Class", "\"Class\" IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                });

            migrationBuilder.CreateTable(
                name: "Combos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    FeatId = table.Column<long>(type: "bigint", nullable: true),
                    GameActionId = table.Column<long>(type: "bigint", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<byte>(type: "smallint", nullable: false),
                    Cooldown = table.Column<int>(type: "integer", nullable: false),
                    Criteria = table.Column<int>(type: "integer", nullable: false),
                    Quality = table.Column<int>(type: "integer", nullable: false),
                    Stamina = table.Column<int>(type: "integer", nullable: false),
                    Actions = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Animation = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Combos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatIdMaps",
                columns: table => new
                {
                    WireFeatId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TemplateId = table.Column<long>(type: "bigint", nullable: false),
                    RootFlag = table.Column<long>(type: "bigint", nullable: false),
                    BaseIndex = table.Column<long>(type: "bigint", nullable: false),
                    Rank = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatIdMaps", x => x.WireFeatId);
                });

            migrationBuilder.CreateTable(
                name: "Feats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Flags = table.Column<long>(type: "bigint", nullable: false),
                    TemplateCategory = table.Column<long>(type: "bigint", nullable: false),
                    Group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Tree = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BaseName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Maps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    InstanceMode = table.Column<int>(type: "integer", nullable: false),
                    PlayfieldInstanced = table.Column<bool>(type: "boolean", nullable: false),
                    PopulationCap = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PlayfieldFieldA = table.Column<long>(type: "bigint", nullable: false),
                    PlayfieldCount = table.Column<long>(type: "bigint", nullable: false),
                    PlayfieldFieldB = table.Column<long>(type: "bigint", nullable: false),
                    PlayfieldFieldC = table.Column<long>(type: "bigint", nullable: false),
                    PlayfieldFlags = table.Column<long>(type: "bigint", nullable: false),
                    PlayfieldToken = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MorphTargets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<byte>(type: "smallint", nullable: false),
                    Weight = table.Column<float>(type: "real", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MorphTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NpcTemplates",
                columns: table => new
                {
                    TemplateId = table.Column<long>(type: "bigint", nullable: false),
                    DeathBehaviorId = table.Column<long>(type: "bigint", nullable: true),
                    LootContainerTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcTemplates", x => x.TemplateId);
                    table.CheckConstraint("CK_NpcTemplate_LootContainerTemplateId", "\"LootContainerTemplateId\" IN (13781, 13782)");
                });

            migrationBuilder.CreateTable(
                name: "QuestAreas",
                columns: table => new
                {
                    AreaId = table.Column<long>(type: "bigint", nullable: false),
                    AreaTitleRefId = table.Column<long>(type: "bigint", nullable: false),
                    MapRefId = table.Column<long>(type: "bigint", nullable: false),
                    RegionRefId = table.Column<long>(type: "bigint", nullable: false),
                    AreaTitleKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MapKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RegionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AreaTitleCategory = table.Column<long>(type: "bigint", nullable: false),
                    Magic = table.Column<long>(type: "bigint", nullable: false),
                    MapCategory = table.Column<long>(type: "bigint", nullable: false),
                    RegionCategory = table.Column<long>(type: "bigint", nullable: false),
                    TableField1 = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestAreas", x => x.AreaId);
                });

            migrationBuilder.CreateTable(
                name: "Realms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Country = table.Column<byte>(type: "smallint", nullable: false),
                    LoadStatus = table.Column<byte>(type: "smallint", nullable: false),
                    OnlineStatus = table.Column<byte>(type: "smallint", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ChatServerPort = table.Column<int>(type: "integer", nullable: false),
                    CsPlayerAgentPort = table.Column<int>(type: "integer", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Realms", x => x.Id);
                    table.CheckConstraint("CK_Realm_Country", "\"Country\" IN (0, 1, 2, 3)");
                    table.CheckConstraint("CK_Realm_LoadStatus", "\"LoadStatus\" IN (0, 1, 2, 3, 4)");
                    table.CheckConstraint("CK_Realm_OnlineStatus", "\"OnlineStatus\" IN (0, 2)");
                    table.CheckConstraint("CK_Realm_Type", "\"Type\" IN (0, 1, 256, 257, 1081)");
                });

            migrationBuilder.CreateTable(
                name: "Recipes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    TrailingFlag = table.Column<byte>(type: "smallint", nullable: false),
                    Header1 = table.Column<long>(type: "bigint", nullable: false),
                    MidFlag = table.Column<long>(type: "bigint", nullable: false),
                    RdbType = table.Column<long>(type: "bigint", nullable: false),
                    RefId0 = table.Column<long>(type: "bigint", nullable: false),
                    Tail = table.Column<long>(type: "bigint", nullable: false),
                    RefId1 = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Spells",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<byte>(type: "smallint", nullable: false),
                    Rank = table.Column<byte>(type: "smallint", nullable: false),
                    DamageRating = table.Column<float>(type: "real", nullable: false),
                    Range = table.Column<float>(type: "real", nullable: false),
                    DotDelta = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DotType = table.Column<long>(type: "bigint", nullable: false),
                    DotIntervalMs = table.Column<long>(type: "bigint", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    EffectSpell = table.Column<long>(type: "bigint", nullable: false),
                    CastTime = table.Column<int>(type: "integer", nullable: false),
                    Cooldown = table.Column<int>(type: "integer", nullable: false),
                    ManaCost = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spells", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockPhrases",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Category = table.Column<long>(type: "bigint", nullable: false),
                    Knubot = table.Column<long>(type: "bigint", nullable: false),
                    RemoteFormatId = table.Column<long>(type: "bigint", nullable: false),
                    FormatKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockPhrases", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ClassAbilities",
                columns: table => new
                {
                    AbilityId = table.Column<long>(type: "bigint", nullable: false),
                    Class = table.Column<byte>(type: "smallint", nullable: false),
                    Level = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassAbilities", x => new { x.Class, x.AbilityId });
                    table.CheckConstraint("CK_ClassAbility_Class", "\"Class\" IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                    table.ForeignKey(
                        name: "FK_ClassAbilities_Abilities_AbilityId",
                        column: x => x.AbilityId,
                        principalTable: "Abilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassCombos",
                columns: table => new
                {
                    ComboId = table.Column<long>(type: "bigint", nullable: false),
                    Class = table.Column<byte>(type: "smallint", nullable: false),
                    Level = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassCombos", x => new { x.Class, x.ComboId });
                    table.CheckConstraint("CK_ClassCombo_Class", "\"Class\" IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                    table.ForeignKey(
                        name: "FK_ClassCombos_Combos_ComboId",
                        column: x => x.ComboId,
                        principalTable: "Combos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatPrereqs",
                columns: table => new
                {
                    WireFeatId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    RequiresWireFeatId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatPrereqs", x => new { x.WireFeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatPrereqs_FeatIdMaps_WireFeatId",
                        column: x => x.WireFeatId,
                        principalTable: "FeatIdMaps",
                        principalColumn: "WireFeatId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatEffects",
                columns: table => new
                {
                    FeatId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    StatId = table.Column<long>(type: "bigint", nullable: false),
                    EffectKind = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatEffects", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatEffects_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapAreas",
                columns: table => new
                {
                    AreaNameId = table.Column<long>(type: "bigint", nullable: false),
                    MapId = table.Column<long>(type: "bigint", nullable: false),
                    MaxX = table.Column<float>(type: "real", nullable: false),
                    MaxY = table.Column<float>(type: "real", nullable: false),
                    MaxZ = table.Column<float>(type: "real", nullable: false),
                    MinX = table.Column<float>(type: "real", nullable: false),
                    MinY = table.Column<float>(type: "real", nullable: false),
                    MinZ = table.Column<float>(type: "real", nullable: false),
                    SampleCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapAreas", x => new { x.MapId, x.AreaNameId });
                    table.ForeignKey(
                        name: "FK_MapAreas_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapLootEntries",
                columns: table => new
                {
                    ItemTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    MapId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapLootEntries", x => new { x.MapId, x.ItemTemplateId });
                    table.ForeignKey(
                        name: "FK_MapLootEntries_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RespawnPoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    MapId = table.Column<long>(type: "bigint", nullable: false),
                    AlwaysKnown = table.Column<bool>(type: "boolean", nullable: false),
                    RestorationLevel = table.Column<float>(type: "real", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PositionX = table.Column<float>(type: "real", nullable: false),
                    PositionY = table.Column<float>(type: "real", nullable: false),
                    PositionZ = table.Column<float>(type: "real", nullable: false),
                    RotationW = table.Column<float>(type: "real", nullable: false),
                    RotationX = table.Column<float>(type: "real", nullable: false),
                    RotationY = table.Column<float>(type: "real", nullable: false),
                    RotationZ = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RespawnPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RespawnPoints_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorldObjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    DestinationMapId = table.Column<long>(type: "bigint", nullable: true),
                    EffectQuestId = table.Column<long>(type: "bigint", nullable: true),
                    GateQuestId = table.Column<long>(type: "bigint", nullable: true),
                    MapId = table.Column<long>(type: "bigint", nullable: false),
                    ObjectId = table.Column<long>(type: "bigint", nullable: false),
                    TemplateItemId = table.Column<long>(type: "bigint", nullable: true),
                    InteractAction = table.Column<long>(type: "bigint", nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    ShowPopupOnUse = table.Column<bool>(type: "boolean", nullable: true),
                    EffectDynelState = table.Column<byte>(type: "smallint", nullable: false),
                    GeometryAssetId = table.Column<long>(type: "bigint", nullable: false),
                    GeometryAssetType = table.Column<long>(type: "bigint", nullable: false),
                    GeometryName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BoundsMinX = table.Column<float>(type: "real", nullable: false),
                    BoundsMinY = table.Column<float>(type: "real", nullable: false),
                    BoundsMinZ = table.Column<float>(type: "real", nullable: false),
                    BoundsMaxX = table.Column<float>(type: "real", nullable: false),
                    BoundsMaxY = table.Column<float>(type: "real", nullable: false),
                    BoundsMaxZ = table.Column<float>(type: "real", nullable: false),
                    GeometryWord0 = table.Column<long>(type: "bigint", nullable: false),
                    GeometryWord1 = table.Column<long>(type: "bigint", nullable: false),
                    GeometryFlag = table.Column<bool>(type: "boolean", nullable: false),
                    DestinationX = table.Column<float>(type: "real", nullable: true),
                    DestinationY = table.Column<float>(type: "real", nullable: true),
                    DestinationZ = table.Column<float>(type: "real", nullable: true),
                    DisplayKind = table.Column<byte>(type: "smallint", nullable: true),
                    DisplayId = table.Column<long>(type: "bigint", nullable: true),
                    DisplayToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayCategory = table.Column<long>(type: "bigint", nullable: true),
                    DisplayFlags = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DisplayKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayKnubot = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CarsGroup = table.Column<long>(type: "bigint", nullable: false),
                    Kdtree = table.Column<long>(type: "bigint", nullable: false),
                    SubOpcode = table.Column<long>(type: "bigint", nullable: false),
                    QuestItemEffect = table.Column<long>(type: "bigint", nullable: true),
                    WaypointType = table.Column<long>(type: "bigint", nullable: true),
                    PositionX = table.Column<float>(type: "real", nullable: false),
                    PositionY = table.Column<float>(type: "real", nullable: false),
                    PositionZ = table.Column<float>(type: "real", nullable: false),
                    RotationW = table.Column<float>(type: "real", nullable: false),
                    RotationX = table.Column<float>(type: "real", nullable: false),
                    RotationY = table.Column<float>(type: "real", nullable: false),
                    RotationZ = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldObjects", x => x.Id);
                    table.CheckConstraint("CK_WorldObject_DisplayKind", "\"DisplayKind\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_WorldObjects_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcChatter",
                columns: table => new
                {
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SayInChat = table.Column<long>(type: "bigint", nullable: false),
                    ChannelCount = table.Column<long>(type: "bigint", nullable: false),
                    Group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Category = table.Column<long>(type: "bigint", nullable: false),
                    Knubot = table.Column<long>(type: "bigint", nullable: false),
                    FormatKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParameterName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcChatter", x => new { x.NpcTemplateId, x.Channel, x.SayInChat });
                    table.ForeignKey(
                        name: "FK_NpcChatter_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcMorphs",
                columns: table => new
                {
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    MorphId = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<float>(type: "real", nullable: false),
                    Text = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcMorphs", x => new { x.NpcTemplateId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_NpcMorphs_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpawnAreas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    CommunityId = table.Column<long>(type: "bigint", nullable: true),
                    MapId = table.Column<long>(type: "bigint", nullable: false),
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: true),
                    ResolvedBy = table.Column<int>(type: "integer", nullable: false),
                    BoundsMaxX = table.Column<float>(type: "real", nullable: false),
                    BoundsMaxY = table.Column<float>(type: "real", nullable: false),
                    BoundsMaxZ = table.Column<float>(type: "real", nullable: false),
                    BoundsMinX = table.Column<float>(type: "real", nullable: false),
                    BoundsMinY = table.Column<float>(type: "real", nullable: false),
                    BoundsMinZ = table.Column<float>(type: "real", nullable: false),
                    GroupOrdinal = table.Column<int>(type: "integer", nullable: false),
                    MaxCount = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GroupKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GroupName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcSpawnAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NpcSpawnAreas_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NpcSpawnAreas_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpells",
                columns: table => new
                {
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    SpellId = table.Column<long>(type: "bigint", nullable: false),
                    FlagA = table.Column<byte>(type: "smallint", nullable: false),
                    FlagB = table.Column<byte>(type: "smallint", nullable: false),
                    FlagC = table.Column<byte>(type: "smallint", nullable: false),
                    Magnitude = table.Column<byte>(type: "smallint", nullable: false),
                    Rank = table.Column<byte>(type: "smallint", nullable: false),
                    Duration = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcSpells", x => new { x.NpcTemplateId, x.SpellId });
                    table.ForeignKey(
                        name: "FK_NpcSpells_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NpcStatCores",
                columns: table => new
                {
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    IirHeader = table.Column<long>(type: "bigint", nullable: true),
                    MinimalPostIirWord2 = table.Column<long>(type: "bigint", nullable: true),
                    Race = table.Column<long>(type: "bigint", nullable: true),
                    Sex = table.Column<byte>(type: "smallint", nullable: true),
                    Side = table.Column<long>(type: "bigint", nullable: true),
                    Flags = table.Column<long>(type: "bigint", nullable: true),
                    HeadMesh = table.Column<long>(type: "bigint", nullable: true),
                    MonsterData = table.Column<long>(type: "bigint", nullable: true),
                    MonsterScale = table.Column<long>(type: "bigint", nullable: true),
                    Radius = table.Column<int>(type: "integer", nullable: true),
                    ChatId = table.Column<long>(type: "bigint", nullable: true),
                    GuildId = table.Column<long>(type: "bigint", nullable: true),
                    CarsGroup = table.Column<long>(type: "bigint", nullable: true),
                    OrientationMode = table.Column<long>(type: "bigint", nullable: true),
                    SoundScryScript = table.Column<long>(type: "bigint", nullable: true),
                    ClientScryScript = table.Column<long>(type: "bigint", nullable: true),
                    MountMesh = table.Column<long>(type: "bigint", nullable: true),
                    Skulk = table.Column<long>(type: "bigint", nullable: true),
                    MaxSkulk = table.Column<long>(type: "bigint", nullable: true),
                    TradeskillFlags = table.Column<long>(type: "bigint", nullable: true),
                    OocMovementMultiplier = table.Column<long>(type: "bigint", nullable: true),
                    RunSpeed = table.Column<long>(type: "bigint", nullable: true),
                    MaxRunSpeed = table.Column<long>(type: "bigint", nullable: true),
                    Bcc = table.Column<long>(type: "bigint", nullable: true),
                    BccState = table.Column<long>(type: "bigint", nullable: true),
                    BloodType = table.Column<long>(type: "bigint", nullable: true),
                    LooksPackage = table.Column<long>(type: "bigint", nullable: true),
                    MountFlags = table.Column<long>(type: "bigint", nullable: true),
                    MountScale = table.Column<long>(type: "bigint", nullable: true),
                    MovementMultiplier = table.Column<long>(type: "bigint", nullable: true),
                    VoiceSet = table.Column<long>(type: "bigint", nullable: true),
                    ApprenticeLevel = table.Column<long>(type: "bigint", nullable: true),
                    Level = table.Column<long>(type: "bigint", nullable: true),
                    Health = table.Column<long>(type: "bigint", nullable: true),
                    Life = table.Column<long>(type: "bigint", nullable: true),
                    Mana = table.Column<long>(type: "bigint", nullable: true),
                    MaxMana = table.Column<long>(type: "bigint", nullable: true),
                    Stamina = table.Column<long>(type: "bigint", nullable: true),
                    MaxStamina = table.Column<long>(type: "bigint", nullable: true),
                    Community = table.Column<long>(type: "bigint", nullable: true),
                    Disposition = table.Column<long>(type: "bigint", nullable: true),
                    TemplateId = table.Column<long>(type: "bigint", nullable: true),
                    NpcType = table.Column<long>(type: "bigint", nullable: true),
                    NpcFlags = table.Column<long>(type: "bigint", nullable: true),
                    Normalset = table.Column<long>(type: "bigint", nullable: true),
                    SuperFamily = table.Column<long>(type: "bigint", nullable: true),
                    ProfessionName = table.Column<long>(type: "bigint", nullable: true),
                    ProfessionType = table.Column<long>(type: "bigint", nullable: true),
                    SightSkill = table.Column<long>(type: "bigint", nullable: true),
                    HearingSkill = table.Column<long>(type: "bigint", nullable: true),
                    SmellingSkill = table.Column<long>(type: "bigint", nullable: true),
                    SpawnAnimation = table.Column<long>(type: "bigint", nullable: true),
                    LosHeight = table.Column<long>(type: "bigint", nullable: true),
                    FactionId = table.Column<long>(type: "bigint", nullable: true),
                    AnimActionId = table.Column<long>(type: "bigint", nullable: true),
                    AnimActionTimer = table.Column<float>(type: "real", nullable: true),
                    AnimActionBlend = table.Column<bool>(type: "boolean", nullable: true),
                    AnimIdleId = table.Column<long>(type: "bigint", nullable: true),
                    AnimIdleTimer = table.Column<float>(type: "real", nullable: true),
                    AnimIdleBlend = table.Column<bool>(type: "boolean", nullable: true),
                    AnimForcedId = table.Column<long>(type: "bigint", nullable: true),
                    AnimForcedTimer = table.Column<float>(type: "real", nullable: true),
                    AnimForcedBlend = table.Column<bool>(type: "boolean", nullable: true),
                    AnimFlags = table.Column<long>(type: "bigint", nullable: true),
                    CanFall = table.Column<bool>(type: "boolean", nullable: true),
                    MovementMode = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcStatCores", x => x.NpcTemplateId);
                    table.CheckConstraint("CK_NpcStatCore_Sex", "\"Sex\" IN (0, 2, 3)");
                    table.ForeignKey(
                        name: "FK_NpcStatCores_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Shops",
                columns: table => new
                {
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    VendorName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaxSize = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shops", x => x.NpcTemplateId);
                    table.ForeignKey(
                        name: "FK_Shops_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuestDefinitions",
                columns: table => new
                {
                    QuestId = table.Column<long>(type: "bigint", nullable: false),
                    AreaId = table.Column<long>(type: "bigint", nullable: false),
                    GoalId = table.Column<long>(type: "bigint", nullable: false),
                    MapId = table.Column<long>(type: "bigint", nullable: false),
                    NextQuestId = table.Column<long>(type: "bigint", nullable: true),
                    HasSnapshotRecord = table.Column<bool>(type: "boolean", nullable: false),
                    ObjectiveRegionVerified = table.Column<bool>(type: "boolean", nullable: false),
                    IndexGoalSteps = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IndexMagic = table.Column<long>(type: "bigint", nullable: false),
                    IndexMarker = table.Column<long>(type: "bigint", nullable: false),
                    IndexTableField1 = table.Column<long>(type: "bigint", nullable: false),
                    IndexTableField2 = table.Column<long>(type: "bigint", nullable: false),
                    IndexTableField3 = table.Column<long>(type: "bigint", nullable: false),
                    IndexTrailerWord = table.Column<long>(type: "bigint", nullable: false),
                    JournalSequence = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<long>(type: "bigint", nullable: false),
                    MarkerX = table.Column<long>(type: "bigint", nullable: false),
                    MarkerY = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUnknown118 = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUnknown14 = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUnknown28 = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUnknownEc = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUnknownF0 = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUnknownF8 = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestDefinitions", x => x.QuestId);
                    table.ForeignKey(
                        name: "FK_QuestDefinitions_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuestDefinitions_QuestAreas_AreaId",
                        column: x => x.AreaId,
                        principalTable: "QuestAreas",
                        principalColumn: "AreaId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Guilds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RealmId = table.Column<long>(type: "bigint", nullable: false),
                    CreationDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GovernmentForm = table.Column<byte>(type: "smallint", nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MessageOfTheDay = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Level = table.Column<long>(type: "bigint", nullable: false),
                    Renown = table.Column<long>(type: "bigint", nullable: false),
                    Cash = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    RenownArtistry = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    RenownGlory = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    RenownValor = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Guilds", x => x.Id);
                    table.CheckConstraint("CK_Guild_GovernmentForm", "\"GovernmentForm\" IN (0, 1, 2, 3, 4, 5, 6)");
                    table.ForeignKey(
                        name: "FK_Guilds_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Mails",
                columns: table => new
                {
                    MailId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    RealmId = table.Column<long>(type: "bigint", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    BodyXml = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SenderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FooterWord = table.Column<long>(type: "bigint", nullable: false),
                    RecipientClass = table.Column<long>(type: "bigint", nullable: false),
                    SenderBankSlot = table.Column<long>(type: "bigint", nullable: false),
                    SenderClass = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp1 = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp2 = table.Column<long>(type: "bigint", nullable: false),
                    Money = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mails", x => x.MailId);
                    table.ForeignKey(
                        name: "FK_Mails_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassSpells",
                columns: table => new
                {
                    SpellId = table.Column<long>(type: "bigint", nullable: false),
                    Class = table.Column<byte>(type: "smallint", nullable: false),
                    IsStarter = table.Column<bool>(type: "boolean", nullable: false),
                    GrantLevel = table.Column<byte>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassSpells", x => new { x.Class, x.SpellId });
                    table.CheckConstraint("CK_ClassSpell_Class", "\"Class\" IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                    table.ForeignKey(
                        name: "FK_ClassSpells_Spells_SpellId",
                        column: x => x.SpellId,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Key1 = table.Column<long>(type: "bigint", nullable: false),
                    Key2 = table.Column<long>(type: "bigint", nullable: false),
                    Key3 = table.Column<long>(type: "bigint", nullable: false),
                    Key4 = table.Column<long>(type: "bigint", nullable: false),
                    Quality = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false, defaultValue: 104),
                    ExcludeFromRandomLoot = table.Column<bool>(type: "boolean", nullable: false),
                    Level = table.Column<byte>(type: "smallint", nullable: false),
                    MaxDamage = table.Column<int>(type: "integer", nullable: true),
                    MinDamage = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Hash = table.Column<long>(type: "bigint", nullable: false),
                    SearchCategory = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false),
                    BagExtraSlots = table.Column<long>(type: "bigint", nullable: true),
                    OnUseSpell = table.Column<long>(type: "bigint", nullable: true),
                    ResourceBagExtraSlots = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.CheckConstraint("CK_Item_Quality", "\"Quality\" IN (0, 1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_Item_Type", "\"Type\" IN (104, 112, 113)");
                    table.ForeignKey(
                        name: "FK_Items_Spells_OnUseSpell",
                        column: x => x.OnUseSpell,
                        principalTable: "Spells",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SpellEffects",
                columns: table => new
                {
                    SpellId = table.Column<long>(type: "bigint", nullable: false),
                    Trigger = table.Column<byte>(type: "smallint", nullable: false),
                    Sequence = table.Column<byte>(type: "smallint", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    TargetSpellId = table.Column<long>(type: "bigint", nullable: true),
                    Template = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Combat = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    MaxDurationMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellEffects", x => new { x.SpellId, x.Trigger, x.Sequence });
                    table.CheckConstraint("CK_SpellEffect_Kind", "\"Kind\" IN (0, 1)");
                    table.CheckConstraint("CK_SpellEffect_Trigger", "\"Trigger\" IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_SpellEffects_Spells_SpellId",
                        column: x => x.SpellId,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpellStatEffects",
                columns: table => new
                {
                    SpellId = table.Column<long>(type: "bigint", nullable: false),
                    StatId = table.Column<long>(type: "bigint", nullable: false),
                    EffectKind = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellStatEffects", x => new { x.SpellId, x.StatId });
                    table.ForeignKey(
                        name: "FK_SpellStatEffects_Spells_SpellId",
                        column: x => x.SpellId,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorldObjectWords",
                columns: table => new
                {
                    WorldObjectId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldObjectWords", x => new { x.WorldObjectId, x.Kind, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_WorldObjectWords_WorldObjects_WorldObjectId",
                        column: x => x.WorldObjectId,
                        principalTable: "WorldObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpawnAreaPoints",
                columns: table => new
                {
                    NpcSpawnAreaId = table.Column<long>(type: "bigint", nullable: false),
                    PointIndex = table.Column<int>(type: "integer", nullable: false),
                    PositionX = table.Column<float>(type: "real", nullable: false),
                    PositionY = table.Column<float>(type: "real", nullable: false),
                    PositionZ = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcSpawnAreaPoints", x => new { x.NpcSpawnAreaId, x.PointIndex });
                    table.ForeignKey(
                        name: "FK_NpcSpawnAreaPoints_NpcSpawnAreas_NpcSpawnAreaId",
                        column: x => x.NpcSpawnAreaId,
                        principalTable: "NpcSpawnAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpawns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: true),
                    MapId = table.Column<long>(type: "bigint", nullable: false),
                    NpcSpawnAreaId = table.Column<long>(type: "bigint", nullable: true),
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    HasEquipment = table.Column<bool>(type: "boolean", nullable: false),
                    ApprenticeLevel = table.Column<long>(type: "bigint", nullable: false),
                    HeadMesh = table.Column<long>(type: "bigint", nullable: false),
                    Health = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<long>(type: "bigint", nullable: false),
                    Life = table.Column<long>(type: "bigint", nullable: false),
                    Mana = table.Column<long>(type: "bigint", nullable: false),
                    MaxMana = table.Column<long>(type: "bigint", nullable: false),
                    MaxStamina = table.Column<long>(type: "bigint", nullable: false),
                    MonsterData = table.Column<long>(type: "bigint", nullable: false),
                    MonsterScale = table.Column<long>(type: "bigint", nullable: false),
                    Stamina = table.Column<long>(type: "bigint", nullable: false),
                    VoiceSet = table.Column<long>(type: "bigint", nullable: false),
                    HearingSkill = table.Column<long>(type: "bigint", nullable: true),
                    SightSkill = table.Column<long>(type: "bigint", nullable: true),
                    SmellingSkill = table.Column<long>(type: "bigint", nullable: true),
                    WireHealth = table.Column<long>(type: "bigint", nullable: true),
                    PositionX = table.Column<float>(type: "real", nullable: false),
                    PositionY = table.Column<float>(type: "real", nullable: false),
                    PositionZ = table.Column<float>(type: "real", nullable: false),
                    RotationW = table.Column<float>(type: "real", nullable: false),
                    RotationX = table.Column<float>(type: "real", nullable: false),
                    RotationY = table.Column<float>(type: "real", nullable: false),
                    RotationZ = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcSpawns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NpcSpawns_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcSpawns_NpcSpawnAreas_NpcSpawnAreaId",
                        column: x => x.NpcSpawnAreaId,
                        principalTable: "NpcSpawnAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NpcSpawns_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationNodes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    ConditionQuestId = table.Column<long>(type: "bigint", nullable: true),
                    GrantQuestId = table.Column<long>(type: "bigint", nullable: true),
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: true),
                    TypeId = table.Column<long>(type: "bigint", nullable: false),
                    ConditionQuestState = table.Column<int>(type: "integer", nullable: true),
                    AnswerPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NpcName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationNodes_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationNodes_QuestDefinitions_GrantQuestId",
                        column: x => x.GrantQuestId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId");
                });

            migrationBuilder.CreateTable(
                name: "QuestTextRefs",
                columns: table => new
                {
                    QuestId = table.Column<long>(type: "bigint", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    RefId = table.Column<long>(type: "bigint", nullable: false),
                    IsLocalized = table.Column<bool>(type: "boolean", nullable: false),
                    KeyText = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Flags = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Category = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestTextRefs", x => new { x.QuestId, x.Slot });
                    table.ForeignKey(
                        name: "FK_QuestTextRefs_QuestDefinitions_QuestId",
                        column: x => x.QuestId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<long>(type: "bigint", nullable: false),
                    BindRespawnPointId = table.Column<long>(type: "bigint", nullable: true),
                    GuildId = table.Column<long>(type: "bigint", nullable: true),
                    MapId = table.Column<long>(type: "bigint", nullable: false),
                    MountId = table.Column<long>(type: "bigint", nullable: true),
                    RealmId = table.Column<long>(type: "bigint", nullable: false),
                    Class = table.Column<byte>(type: "smallint", nullable: false),
                    CreationDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastConnection = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GuildRank = table.Column<byte>(type: "smallint", nullable: true),
                    Race = table.Column<byte>(type: "smallint", nullable: false),
                    Sex = table.Column<byte>(type: "smallint", nullable: false),
                    ActiveWeaponSet = table.Column<int>(type: "integer", nullable: false),
                    IsDead = table.Column<bool>(type: "boolean", nullable: false),
                    AuthChallenge = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    LastIpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Experience = table.Column<long>(type: "bigint", nullable: false),
                    Money = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Kills = table.Column<long>(type: "bigint", nullable: false),
                    Deaths = table.Column<long>(type: "bigint", nullable: false),
                    TutorialStepsSeen = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    PositionX = table.Column<float>(type: "real", nullable: false),
                    PositionY = table.Column<float>(type: "real", nullable: false),
                    PositionZ = table.Column<float>(type: "real", nullable: false),
                    RotationW = table.Column<float>(type: "real", nullable: false),
                    RotationX = table.Column<float>(type: "real", nullable: false),
                    RotationY = table.Column<float>(type: "real", nullable: false),
                    RotationZ = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                    table.CheckConstraint("CK_Character_Class", "\"Class\" IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                    table.CheckConstraint("CK_Character_GuildRank", "\"GuildRank\" IN (0, 4)");
                    table.CheckConstraint("CK_Character_Race", "\"Race\" IN (1, 2, 3, 10)");
                    table.CheckConstraint("CK_Character_Sex", "\"Sex\" IN (0, 2, 3)");
                    table.ForeignKey(
                        name: "FK_Characters_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Characters_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Characters_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Characters_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Characters_RespawnPoints_BindRespawnPointId",
                        column: x => x.BindRespawnPointId,
                        principalTable: "RespawnPoints",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AccountClaims",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<long>(type: "bigint", nullable: false),
                    ItemTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    ClaimType = table.Column<long>(type: "bigint", nullable: false),
                    MaxCharges = table.Column<long>(type: "bigint", nullable: true),
                    Quantity = table.Column<long>(type: "bigint", nullable: true),
                    Level = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountClaims_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountClaims_Items_ItemTemplateId",
                        column: x => x.ItemTemplateId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemStatModifiers",
                columns: table => new
                {
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    StatId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemStatModifiers", x => new { x.ItemId, x.StatId });
                    table.CheckConstraint("CK_ItemStatModifier_StatId", "\"StatId\" IN (0, 1, 2, 11, 12, 13, 20, 21, 23, 25, 27, 33, 34, 37, 39, 47, 51, 52, 54, 57, 58, 59, 60, 61, 70, 75, 79, 90, 91, 100, 102, 104, 106, 110, 127, 131, 135, 139, 140, 141, 143, 149, 157, 158, 162, 165, 167, 169, 170, 173, 174, 186, 187, 191, 215, 216, 217, 218, 219, 220, 222, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 238, 240, 241, 243, 254, 255, 260, 269, 271, 275, 278, 279, 280, 281, 282, 287, 291, 292, 296, 300, 301, 308, 310, 311, 312, 319, 320, 326, 330, 331, 334, 346, 350, 353, 355, 360, 363, 375, 380, 389, 448, 450, 451, 454, 456, 458, 465, 475, 476, 477, 480, 488, 490, 491, 493, 498, 504, 505, 506, 507, 536, 560, 561, 577, 594, 595, 596, 597, 607, 656, 658, 666, 667, 668, 669, 670, 671, 690, 711, 713, 804, 805, 806, 807, 808, 809, 810, 811, 812, 813, 814, 815, 816, 817, 818, 819, 821, 823, 824, 825, 827, 828, 829, 830, 831, 833, 835, 837, 840, 861, 864, 866, 867, 868, 869, 870, 871, 872, 873, 874, 875, 876, 877, 878, 879, 880, 899, 901, 902, 903, 904, 905, 906, 907, 908, 909, 920, 922, 923, 925, 926, 927, 928, 929, 931, 932, 933, 934, 935, 936, 937, 938, 939, 940, 941, 942, 943, 944, 945, 946, 948, 965, 1007, 1008, 1009, 1010, 1035, 1037, 1040, 1071, 1085, 1093, 1095, 1096, 1097, 1098, 1099, 1332, 1376, 1378, 1380, 1382, 1383, 1385, 1386, 1387, 1390, 1392, 1393, 1395, 1397, 1403)");
                    table.ForeignKey(
                        name: "FK_ItemStatModifiers_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailAttachments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemInstanceId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MailId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    ItemHash = table.Column<long>(type: "bigint", nullable: false),
                    ItemLevel = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    Template = table.Column<long>(type: "bigint", nullable: false),
                    TrailerClass = table.Column<long>(type: "bigint", nullable: false),
                    TrailerTail = table.Column<long>(type: "bigint", nullable: false),
                    TrailerWord = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailAttachments_Items_Template",
                        column: x => x.Template,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailAttachments_Mails_MailId",
                        column: x => x.MailId,
                        principalTable: "Mails",
                        principalColumn: "MailId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcEquipment",
                columns: table => new
                {
                    NpcTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    Pos = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<long>(type: "bigint", nullable: false),
                    Template = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcEquipment", x => new { x.NpcTemplateId, x.Pos });
                    table.ForeignKey(
                        name: "FK_NpcEquipment_Items_Template",
                        column: x => x.Template,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NpcEquipment_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestGoalDefinitions",
                columns: table => new
                {
                    GoalId = table.Column<long>(type: "bigint", nullable: false),
                    MapId = table.Column<long>(type: "bigint", nullable: true),
                    ObjectiveRefId = table.Column<long>(type: "bigint", nullable: false),
                    QuestDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    QuestItemId = table.Column<long>(type: "bigint", nullable: true),
                    SubId = table.Column<long>(type: "bigint", nullable: false),
                    TrailingRefId = table.Column<long>(type: "bigint", nullable: false),
                    GoalType = table.Column<int>(type: "integer", nullable: false),
                    TrailingLocalized = table.Column<bool>(type: "boolean", nullable: false),
                    ObjectiveKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ObjectiveToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TrailingFlags = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TrailingKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TrailingToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Extra0 = table.Column<long>(type: "bigint", nullable: false),
                    Extra1 = table.Column<long>(type: "bigint", nullable: false),
                    Extra2 = table.Column<long>(type: "bigint", nullable: false),
                    Extra3 = table.Column<long>(type: "bigint", nullable: false),
                    Extra4 = table.Column<long>(type: "bigint", nullable: false),
                    Extra5 = table.Column<long>(type: "bigint", nullable: false),
                    Extra6 = table.Column<long>(type: "bigint", nullable: false),
                    GoalIdEcho = table.Column<long>(type: "bigint", nullable: false),
                    ObjectiveCategory = table.Column<long>(type: "bigint", nullable: false),
                    RegionOrder = table.Column<long>(type: "bigint", nullable: false),
                    RequiredCount = table.Column<long>(type: "bigint", nullable: false),
                    Separator = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    TrailingCategory = table.Column<long>(type: "bigint", nullable: false),
                    Unknown1 = table.Column<long>(type: "bigint", nullable: false),
                    Unknown2 = table.Column<long>(type: "bigint", nullable: false),
                    MarkerX = table.Column<long>(type: "bigint", nullable: true),
                    MarkerY = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestGoalDefinitions", x => x.GoalId);
                    table.ForeignKey(
                        name: "FK_QuestGoalDefinitions_Items_QuestItemId",
                        column: x => x.QuestItemId,
                        principalTable: "Items",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuestGoalDefinitions_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuestGoalDefinitions_QuestDefinitions_QuestDefinitionId",
                        column: x => x.QuestDefinitionId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeItems",
                columns: table => new
                {
                    RecipeId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<byte>(type: "smallint", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Flag = table.Column<long>(type: "bigint", nullable: false),
                    Trailing = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeItems", x => new { x.RecipeId, x.Role, x.Position });
                    table.CheckConstraint("CK_RecipeItem_Role", "\"Role\" IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_RecipeItems_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeItems_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopItems",
                columns: table => new
                {
                    ShopId = table.Column<long>(type: "bigint", nullable: false),
                    Pos = table.Column<long>(type: "bigint", nullable: false),
                    ItemTemplateId = table.Column<long>(type: "bigint", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Stat356 = table.Column<long>(type: "bigint", nullable: false),
                    TailLevel = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopItems", x => new { x.ShopId, x.Pos });
                    table.ForeignKey(
                        name: "FK_ShopItems_Items_ItemTemplateId",
                        column: x => x.ItemTemplateId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopItems_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "NpcTemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TradepostListings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    RealmId = table.Column<long>(type: "bigint", nullable: false),
                    SaleId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    SellerName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BankSlot = table.Column<long>(type: "bigint", nullable: false),
                    EntryFlags = table.Column<long>(type: "bigint", nullable: false),
                    Handle = table.Column<long>(type: "bigint", nullable: false),
                    ItemHandle = table.Column<long>(type: "bigint", nullable: false),
                    ItemHash = table.Column<long>(type: "bigint", nullable: false),
                    ItemLevel = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    Template = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    InstanceField = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradepostListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradepostListings_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradepostListings_Items_Template",
                        column: x => x.Template,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradepostListings_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpawnEquipment",
                columns: table => new
                {
                    NpcSpawnId = table.Column<long>(type: "bigint", nullable: false),
                    Pos = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<long>(type: "bigint", nullable: false),
                    Template = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcSpawnEquipment", x => new { x.NpcSpawnId, x.Pos });
                    table.ForeignKey(
                        name: "FK_NpcSpawnEquipment_Items_Template",
                        column: x => x.Template,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NpcSpawnEquipment_NpcSpawns_NpcSpawnId",
                        column: x => x.NpcSpawnId,
                        principalTable: "NpcSpawns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpawnMorphs",
                columns: table => new
                {
                    NpcSpawnId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    MorphId = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<float>(type: "real", nullable: false),
                    Text = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcSpawnMorphs", x => new { x.NpcSpawnId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_NpcSpawnMorphs_NpcSpawns_NpcSpawnId",
                        column: x => x.NpcSpawnId,
                        principalTable: "NpcSpawns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolNodes",
                columns: table => new
                {
                    OrdinalPath = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NpcSpawnId = table.Column<long>(type: "bigint", nullable: false),
                    ParentPath = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Mode = table.Column<byte>(type: "smallint", nullable: false),
                    Flag = table.Column<byte>(type: "smallint", nullable: true),
                    HeaderFlags = table.Column<byte>(type: "smallint", nullable: true),
                    ListMin = table.Column<byte>(type: "smallint", nullable: true),
                    PathFlags = table.Column<byte>(type: "smallint", nullable: true),
                    TrailerByte = table.Column<byte>(type: "smallint", nullable: true),
                    ApproachFlag = table.Column<byte>(type: "smallint", nullable: true),
                    AnchorX = table.Column<float>(type: "real", nullable: true),
                    AnchorY = table.Column<float>(type: "real", nullable: true),
                    AnchorZ = table.Column<float>(type: "real", nullable: true),
                    Duration = table.Column<float>(type: "real", nullable: true),
                    Remaining = table.Column<float>(type: "real", nullable: true),
                    Speed = table.Column<float>(type: "real", nullable: true),
                    PathDirection1 = table.Column<float>(type: "real", nullable: true),
                    PathDirection2 = table.Column<float>(type: "real", nullable: true),
                    PathTrailerWord = table.Column<long>(type: "bigint", nullable: true),
                    TargetX = table.Column<float>(type: "real", nullable: true),
                    TargetY = table.Column<float>(type: "real", nullable: true),
                    TargetZ = table.Column<float>(type: "real", nullable: true),
                    TargetRotationX = table.Column<float>(type: "real", nullable: true),
                    TargetRotationY = table.Column<float>(type: "real", nullable: true),
                    TargetRotationZ = table.Column<float>(type: "real", nullable: true),
                    TargetRotationW = table.Column<float>(type: "real", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    PathPacked1 = table.Column<int>(type: "integer", nullable: true),
                    PathPacked2 = table.Column<int>(type: "integer", nullable: true),
                    RotateTail = table.Column<int>(type: "integer", nullable: true),
                    ApproachTarget = table.Column<long>(type: "bigint", nullable: true),
                    ApproachType = table.Column<long>(type: "bigint", nullable: true),
                    PathFloat10 = table.Column<long>(type: "bigint", nullable: true),
                    PathFloat20 = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolNodes", x => new { x.NpcSpawnId, x.OrdinalPath });
                    table.ForeignKey(
                        name: "FK_PatrolNodes_NpcSpawns_NpcSpawnId",
                        column: x => x.NpcSpawnId,
                        principalTable: "NpcSpawns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatrolNodes_PatrolNodes_NpcSpawnId_ParentPath",
                        columns: x => new { x.NpcSpawnId, x.ParentPath },
                        principalTable: "PatrolNodes",
                        principalColumns: new[] { "NpcSpawnId", "OrdinalPath" });
                });

            migrationBuilder.CreateTable(
                name: "ConversationPackets",
                columns: table => new
                {
                    ConversationNodeId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    SubOpcode = table.Column<long>(type: "bigint", nullable: true),
                    Env = table.Column<byte>(type: "smallint", nullable: true),
                    Flag = table.Column<byte>(type: "smallint", nullable: true),
                    ShotSize = table.Column<byte>(type: "smallint", nullable: true),
                    ShotType = table.Column<byte>(type: "smallint", nullable: true),
                    ShotEmphasis = table.Column<byte>(type: "smallint", nullable: true),
                    ShotFov = table.Column<byte>(type: "smallint", nullable: true),
                    ShotTilt = table.Column<byte>(type: "smallint", nullable: true),
                    Autoface = table.Column<bool>(type: "boolean", nullable: true),
                    HeadPaddingY = table.Column<float>(type: "real", nullable: true),
                    Field = table.Column<long>(type: "bigint", nullable: true),
                    SenderInstance = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationPackets", x => new { x.ConversationNodeId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_ConversationPackets_ConversationNodes_ConversationNodeId",
                        column: x => x.ConversationNodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterAbilities",
                columns: table => new
                {
                    AbilityId = table.Column<long>(type: "bigint", nullable: false),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterAbilities", x => new { x.CharacterId, x.AbilityId });
                    table.ForeignKey(
                        name: "FK_CharacterAbilities_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterBodies",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    Size = table.Column<byte>(type: "smallint", nullable: false),
                    SkinColor = table.Column<byte>(type: "smallint", nullable: false),
                    Voice = table.Column<byte>(type: "smallint", nullable: false),
                    Arm = table.Column<float>(type: "real", nullable: false),
                    Arse = table.Column<float>(type: "real", nullable: false),
                    Bosom = table.Column<float>(type: "real", nullable: false),
                    CheekDepth = table.Column<float>(type: "real", nullable: false),
                    CheekHeight = table.Column<float>(type: "real", nullable: false),
                    CheekWidth = table.Column<float>(type: "real", nullable: false),
                    Chest = table.Column<float>(type: "real", nullable: false),
                    ChinLength = table.Column<float>(type: "real", nullable: false),
                    ChinWidth = table.Column<float>(type: "real", nullable: false),
                    CrookedNose = table.Column<float>(type: "real", nullable: false),
                    EarAngle = table.Column<float>(type: "real", nullable: false),
                    Ears = table.Column<float>(type: "real", nullable: false),
                    EyebrowScale = table.Column<float>(type: "real", nullable: false),
                    EyesAngle = table.Column<float>(type: "real", nullable: false),
                    EyesDepth = table.Column<float>(type: "real", nullable: false),
                    EyesHorizontalPos = table.Column<float>(type: "real", nullable: false),
                    EyesVerticalPos = table.Column<float>(type: "real", nullable: false),
                    FaceLength = table.Column<float>(type: "real", nullable: false),
                    JawWidth = table.Column<float>(type: "real", nullable: false),
                    Leg = table.Column<float>(type: "real", nullable: false),
                    MouthVerticalPos = table.Column<float>(type: "real", nullable: false),
                    MouthWidth = table.Column<float>(type: "real", nullable: false),
                    NoseAngle = table.Column<float>(type: "real", nullable: false),
                    NoseCurve = table.Column<float>(type: "real", nullable: false),
                    NoseLength = table.Column<float>(type: "real", nullable: false),
                    NoseWidth = table.Column<float>(type: "real", nullable: false),
                    Stomach = table.Column<float>(type: "real", nullable: false),
                    Thigh = table.Column<float>(type: "real", nullable: false),
                    HeadMesh = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterBodies", x => x.CharacterId);
                    table.ForeignKey(
                        name: "FK_CharacterBodies_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterBodyResources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterBodyResources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterBodyResources_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterBuffs",
                columns: table => new
                {
                    BuffId = table.Column<long>(type: "bigint", nullable: false),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Nonce = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterBuffs", x => new { x.CharacterId, x.BuffId });
                    table.ForeignKey(
                        name: "FK_CharacterBuffs_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCombos",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    ComboId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCombos", x => new { x.CharacterId, x.ComboId });
                    table.ForeignKey(
                        name: "FK_CharacterCombos_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterEquipment",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    BagId = table.Column<long>(type: "bigint", nullable: true),
                    BeltId = table.Column<long>(type: "bigint", nullable: true),
                    BootsId = table.Column<long>(type: "bigint", nullable: true),
                    ChestId = table.Column<long>(type: "bigint", nullable: true),
                    CloakId = table.Column<long>(type: "bigint", nullable: true),
                    FirstPosBagId = table.Column<long>(type: "bigint", nullable: true),
                    HandsId = table.Column<long>(type: "bigint", nullable: true),
                    HeadId = table.Column<long>(type: "bigint", nullable: true),
                    LegsId = table.Column<long>(type: "bigint", nullable: true),
                    MainHandLeftId = table.Column<long>(type: "bigint", nullable: true),
                    MainHandRightId = table.Column<long>(type: "bigint", nullable: true),
                    NeckId = table.Column<long>(type: "bigint", nullable: true),
                    OffHandLeftId = table.Column<long>(type: "bigint", nullable: true),
                    OffHandRightId = table.Column<long>(type: "bigint", nullable: true),
                    ResourceBagId = table.Column<long>(type: "bigint", nullable: true),
                    RingLeftId = table.Column<long>(type: "bigint", nullable: true),
                    RingRightId = table.Column<long>(type: "bigint", nullable: true),
                    ShoulderId = table.Column<long>(type: "bigint", nullable: true),
                    VanityBeltId = table.Column<long>(type: "bigint", nullable: true),
                    VanityBootsId = table.Column<long>(type: "bigint", nullable: true),
                    VanityCapeId = table.Column<long>(type: "bigint", nullable: true),
                    VanityChestId = table.Column<long>(type: "bigint", nullable: true),
                    VanityHeadId = table.Column<long>(type: "bigint", nullable: true),
                    VanityLegsId = table.Column<long>(type: "bigint", nullable: true),
                    VanityWristId = table.Column<long>(type: "bigint", nullable: true),
                    WristId = table.Column<long>(type: "bigint", nullable: true),
                    VanityShownMask = table.Column<long>(type: "bigint", nullable: false)
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
                name: "CharacterFeatSpecs",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterFeatSpecs", x => new { x.CharacterId, x.Index });
                    table.ForeignKey(
                        name: "FK_CharacterFeatSpecs_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "CharacterInventory",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    Container = table.Column<long>(type: "bigint", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    Count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterInventory", x => new { x.CharacterId, x.Container, x.Slot });
                    table.CheckConstraint("CK_CharacterInventory_Container", "\"Container\" IN (101, 104, 105, 111, 112, 113, 115)");
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

            migrationBuilder.CreateTable(
                name: "CharacterMaxStats",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    Health = table.Column<long>(type: "bigint", nullable: false),
                    Mana = table.Column<long>(type: "bigint", nullable: false),
                    Stamina = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterMaxStats", x => x.CharacterId);
                    table.ForeignKey(
                        name: "FK_CharacterMaxStats_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterQuests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    QuestId = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    GoalTargetName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GoalProgress = table.Column<long>(type: "bigint", nullable: false),
                    GoalRequired = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterQuests", x => x.Id);
                    table.CheckConstraint("CK_CharacterQuest_State", "\"State\" IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_CharacterQuests_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterRespawnPoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    RespawnPointId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRespawnPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterRespawnPoints_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterRespawnPoints_RespawnPoints_RespawnPointId",
                        column: x => x.RespawnPointId,
                        principalTable: "RespawnPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterScaffoldPairs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    PairId = table.Column<long>(type: "bigint", nullable: false),
                    ListIndex = table.Column<byte>(type: "smallint", nullable: false),
                    Value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterScaffoldPairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterScaffoldPairs_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterShortcuts",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    Slot = table.Column<long>(type: "bigint", nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: false),
                    ShortcutType = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterShortcuts", x => new { x.CharacterId, x.Slot });
                    table.ForeignKey(
                        name: "FK_CharacterShortcuts_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterSkills",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    SkillId = table.Column<long>(type: "bigint", nullable: false),
                    Rank = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterSkills", x => new { x.CharacterId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_CharacterSkills_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterSpells",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    SpellId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterSpells", x => new { x.CharacterId, x.SpellId });
                    table.ForeignKey(
                        name: "FK_CharacterSpells_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterSpells_Spells_SpellId",
                        column: x => x.SpellId,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterStats",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    StatId = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterStats", x => new { x.CharacterId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_CharacterStats_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterTags",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    TagId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterTags", x => new { x.CharacterId, x.TagId });
                    table.ForeignKey(
                        name: "FK_CharacterTags_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterQuestGoals",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    GoalId = table.Column<long>(type: "bigint", nullable: false),
                    QuestId = table.Column<long>(type: "bigint", nullable: false),
                    Progress = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterQuestGoals", x => new { x.CharacterId, x.QuestId, x.GoalId });
                    table.ForeignKey(
                        name: "FK_CharacterQuestGoals_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterQuestGoals_QuestDefinitions_QuestId",
                        column: x => x.QuestId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterQuestGoals_QuestGoalDefinitions_GoalId",
                        column: x => x.GoalId,
                        principalTable: "QuestGoalDefinitions",
                        principalColumn: "GoalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolNodeWaypoints",
                columns: table => new
                {
                    NpcSpawnId = table.Column<long>(type: "bigint", nullable: false),
                    OrdinalPath = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    PositionX = table.Column<float>(type: "real", nullable: false),
                    PositionY = table.Column<float>(type: "real", nullable: false),
                    PositionZ = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolNodeWaypoints", x => new { x.NpcSpawnId, x.OrdinalPath, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_PatrolNodeWaypoints_PatrolNodes_NpcSpawnId_OrdinalPath",
                        columns: x => new { x.NpcSpawnId, x.OrdinalPath },
                        principalTable: "PatrolNodes",
                        principalColumns: new[] { "NpcSpawnId", "OrdinalPath" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolNodeWords",
                columns: table => new
                {
                    NpcSpawnId = table.Column<long>(type: "bigint", nullable: false),
                    OrdinalPath = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolNodeWords", x => new { x.NpcSpawnId, x.OrdinalPath, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_PatrolNodeWords_PatrolNodes_NpcSpawnId_OrdinalPath",
                        columns: x => new { x.NpcSpawnId, x.OrdinalPath },
                        principalTable: "PatrolNodes",
                        principalColumns: new[] { "NpcSpawnId", "OrdinalPath" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationScreens",
                columns: table => new
                {
                    ConversationNodeId = table.Column<long>(type: "bigint", nullable: false),
                    PacketSequence = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ScreenId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Category = table.Column<long>(type: "bigint", nullable: false),
                    Knubot = table.Column<long>(type: "bigint", nullable: false),
                    Antitag = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationScreens", x => new { x.ConversationNodeId, x.PacketSequence, x.Sequence });
                    table.ForeignKey(
                        name: "FK_ConversationScreens_ConversationPackets_ConversationNodeId_~",
                        columns: x => new { x.ConversationNodeId, x.PacketSequence },
                        principalTable: "ConversationPackets",
                        principalColumns: new[] { "ConversationNodeId", "Sequence" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterFeats",
                columns: table => new
                {
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    SpecIndex = table.Column<int>(type: "integer", nullable: false),
                    FeatId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterFeats", x => new { x.CharacterId, x.SpecIndex, x.FeatId });
                    table.ForeignKey(
                        name: "FK_CharacterFeats_CharacterFeatSpecs_CharacterId_SpecIndex",
                        columns: x => new { x.CharacterId, x.SpecIndex },
                        principalTable: "CharacterFeatSpecs",
                        principalColumns: new[] { "CharacterId", "Index" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterFeats_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationScreenParameters",
                columns: table => new
                {
                    ConversationNodeId = table.Column<long>(type: "bigint", nullable: false),
                    PacketSequence = table.Column<int>(type: "integer", nullable: false),
                    ScreenSequence = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    LocalizedId = table.Column<long>(type: "bigint", nullable: true),
                    LocalizedToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Category = table.Column<long>(type: "bigint", nullable: true),
                    Flags = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ContentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Text = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationScreenParameters", x => new { x.ConversationNodeId, x.PacketSequence, x.ScreenSequence, x.Sequence });
                    table.CheckConstraint("CK_ConversationScreenParameter_Kind", "\"Kind\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_ConversationScreenParameters_ConversationScreens_Conversati~",
                        columns: x => new { x.ConversationNodeId, x.PacketSequence, x.ScreenSequence },
                        principalTable: "ConversationScreens",
                        principalColumns: new[] { "ConversationNodeId", "PacketSequence", "Sequence" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountClaims_AccountId",
                table: "AccountClaims",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountClaims_ItemTemplateId",
                table: "AccountClaims",
                column: "ItemTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_UserName",
                table: "Accounts",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterBodyResources_CharacterId",
                table: "CharacterBodyResources",
                column: "CharacterId");

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
                name: "IX_CharacterFriends_FriendCharacterId",
                table: "CharacterFriends",
                column: "FriendCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterInventory_ItemId",
                table: "CharacterInventory",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterQuestGoals_GoalId",
                table: "CharacterQuestGoals",
                column: "GoalId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterQuestGoals_QuestId",
                table: "CharacterQuestGoals",
                column: "QuestId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterQuests_CharacterId",
                table: "CharacterQuests",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRespawnPoints_CharacterId",
                table: "CharacterRespawnPoints",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRespawnPoints_RespawnPointId",
                table: "CharacterRespawnPoints",
                column: "RespawnPointId");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_AccountId",
                table: "Characters",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_BindRespawnPointId",
                table: "Characters",
                column: "BindRespawnPointId");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_GuildId",
                table: "Characters",
                column: "GuildId");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_MapId",
                table: "Characters",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_RealmId_Name",
                table: "Characters",
                columns: new[] { "RealmId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterScaffoldPairs_CharacterId",
                table: "CharacterScaffoldPairs",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterSpells_SpellId",
                table: "CharacterSpells",
                column: "SpellId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChannelId",
                table: "ChatMessages",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SentAt",
                table: "ChatMessages",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClassAbilities_AbilityId",
                table: "ClassAbilities",
                column: "AbilityId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassCombos_ComboId",
                table: "ClassCombos",
                column: "ComboId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassSpells_SpellId",
                table: "ClassSpells",
                column: "SpellId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_GrantQuestId",
                table: "ConversationNodes",
                column: "GrantQuestId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_NpcName_TypeId_AnswerPath",
                table: "ConversationNodes",
                columns: new[] { "NpcName", "TypeId", "AnswerPath" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_NpcTemplateId",
                table: "ConversationNodes",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_RealmId_Name",
                table: "Guilds",
                columns: new[] { "RealmId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_OnUseSpell",
                table: "Items",
                column: "OnUseSpell");

            migrationBuilder.CreateIndex(
                name: "IX_MailAttachments_MailId",
                table: "MailAttachments",
                column: "MailId");

            migrationBuilder.CreateIndex(
                name: "IX_MailAttachments_Template",
                table: "MailAttachments",
                column: "Template");

            migrationBuilder.CreateIndex(
                name: "IX_Mails_RealmId",
                table: "Mails",
                column: "RealmId");

            migrationBuilder.CreateIndex(
                name: "IX_Mails_RecipientName",
                table: "Mails",
                column: "RecipientName");

            migrationBuilder.CreateIndex(
                name: "IX_NpcEquipment_Template",
                table: "NpcEquipment",
                column: "Template");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawnAreas_MapId_GroupOrdinal",
                table: "NpcSpawnAreas",
                columns: new[] { "MapId", "GroupOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawnAreas_NpcTemplateId",
                table: "NpcSpawnAreas",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawnEquipment_Template",
                table: "NpcSpawnEquipment",
                column: "Template");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawns_MapId_PositionX_PositionY_PositionZ",
                table: "NpcSpawns",
                columns: new[] { "MapId", "PositionX", "PositionY", "PositionZ" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawns_NpcSpawnAreaId",
                table: "NpcSpawns",
                column: "NpcSpawnAreaId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcSpawns_NpcTemplateId",
                table: "NpcSpawns",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolNodes_NpcSpawnId_ParentPath",
                table: "PatrolNodes",
                columns: new[] { "NpcSpawnId", "ParentPath" });

            migrationBuilder.CreateIndex(
                name: "IX_QuestDefinitions_AreaId",
                table: "QuestDefinitions",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestDefinitions_MapId",
                table: "QuestDefinitions",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestGoalDefinitions_MapId",
                table: "QuestGoalDefinitions",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestGoalDefinitions_QuestDefinitionId",
                table: "QuestGoalDefinitions",
                column: "QuestDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestGoalDefinitions_QuestItemId",
                table: "QuestGoalDefinitions",
                column: "QuestItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Realms_Name",
                table: "Realms",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeItems_ItemId",
                table: "RecipeItems",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RespawnPoints_MapId",
                table: "RespawnPoints",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopItems_ItemTemplateId",
                table: "ShopItems",
                column: "ItemTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_TradepostListings_ItemId",
                table: "TradepostListings",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TradepostListings_RealmId_SaleId",
                table: "TradepostListings",
                columns: new[] { "RealmId", "SaleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradepostListings_Template",
                table: "TradepostListings",
                column: "Template");

            migrationBuilder.CreateIndex(
                name: "IX_WorldObjects_MapId",
                table: "WorldObjects",
                column: "MapId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountClaims");

            migrationBuilder.DropTable(
                name: "AppearanceChoices");

            migrationBuilder.DropTable(
                name: "AppearanceSexOptions");

            migrationBuilder.DropTable(
                name: "CharacterAbilities");

            migrationBuilder.DropTable(
                name: "CharacterBodies");

            migrationBuilder.DropTable(
                name: "CharacterBodyResources");

            migrationBuilder.DropTable(
                name: "CharacterBuffs");

            migrationBuilder.DropTable(
                name: "CharacterCombos");

            migrationBuilder.DropTable(
                name: "CharacterEquipment");

            migrationBuilder.DropTable(
                name: "CharacterFeats");

            migrationBuilder.DropTable(
                name: "CharacterFriends");

            migrationBuilder.DropTable(
                name: "CharacterInventory");

            migrationBuilder.DropTable(
                name: "CharacterMaxStats");

            migrationBuilder.DropTable(
                name: "CharacterQuestGoals");

            migrationBuilder.DropTable(
                name: "CharacterQuests");

            migrationBuilder.DropTable(
                name: "CharacterRespawnPoints");

            migrationBuilder.DropTable(
                name: "CharacterScaffoldPairs");

            migrationBuilder.DropTable(
                name: "CharacterShortcuts");

            migrationBuilder.DropTable(
                name: "CharacterSkills");

            migrationBuilder.DropTable(
                name: "CharacterSpells");

            migrationBuilder.DropTable(
                name: "CharacterStats");

            migrationBuilder.DropTable(
                name: "CharacterTags");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ClassAbilities");

            migrationBuilder.DropTable(
                name: "ClassAttributeBases");

            migrationBuilder.DropTable(
                name: "ClassCombos");

            migrationBuilder.DropTable(
                name: "ClassComboScalings");

            migrationBuilder.DropTable(
                name: "ClassSpells");

            migrationBuilder.DropTable(
                name: "ConversationScreenParameters");

            migrationBuilder.DropTable(
                name: "FeatEffects");

            migrationBuilder.DropTable(
                name: "FeatPrereqs");

            migrationBuilder.DropTable(
                name: "ItemStatModifiers");

            migrationBuilder.DropTable(
                name: "MailAttachments");

            migrationBuilder.DropTable(
                name: "MapAreas");

            migrationBuilder.DropTable(
                name: "MapLootEntries");

            migrationBuilder.DropTable(
                name: "MorphTargets");

            migrationBuilder.DropTable(
                name: "NpcChatter");

            migrationBuilder.DropTable(
                name: "NpcEquipment");

            migrationBuilder.DropTable(
                name: "NpcMorphs");

            migrationBuilder.DropTable(
                name: "NpcSpawnAreaPoints");

            migrationBuilder.DropTable(
                name: "NpcSpawnEquipment");

            migrationBuilder.DropTable(
                name: "NpcSpawnMorphs");

            migrationBuilder.DropTable(
                name: "NpcSpells");

            migrationBuilder.DropTable(
                name: "NpcStatCores");

            migrationBuilder.DropTable(
                name: "PatrolNodeWaypoints");

            migrationBuilder.DropTable(
                name: "PatrolNodeWords");

            migrationBuilder.DropTable(
                name: "QuestTextRefs");

            migrationBuilder.DropTable(
                name: "RecipeItems");

            migrationBuilder.DropTable(
                name: "ShopItems");

            migrationBuilder.DropTable(
                name: "SpellEffects");

            migrationBuilder.DropTable(
                name: "SpellStatEffects");

            migrationBuilder.DropTable(
                name: "StockPhrases");

            migrationBuilder.DropTable(
                name: "TradepostListings");

            migrationBuilder.DropTable(
                name: "WorldObjectWords");

            migrationBuilder.DropTable(
                name: "CharacterFeatSpecs");

            migrationBuilder.DropTable(
                name: "QuestGoalDefinitions");

            migrationBuilder.DropTable(
                name: "Abilities");

            migrationBuilder.DropTable(
                name: "Combos");

            migrationBuilder.DropTable(
                name: "ConversationScreens");

            migrationBuilder.DropTable(
                name: "Feats");

            migrationBuilder.DropTable(
                name: "FeatIdMaps");

            migrationBuilder.DropTable(
                name: "Mails");

            migrationBuilder.DropTable(
                name: "PatrolNodes");

            migrationBuilder.DropTable(
                name: "Recipes");

            migrationBuilder.DropTable(
                name: "Shops");

            migrationBuilder.DropTable(
                name: "WorldObjects");

            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "ConversationPackets");

            migrationBuilder.DropTable(
                name: "NpcSpawns");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Guilds");

            migrationBuilder.DropTable(
                name: "RespawnPoints");

            migrationBuilder.DropTable(
                name: "Spells");

            migrationBuilder.DropTable(
                name: "ConversationNodes");

            migrationBuilder.DropTable(
                name: "NpcSpawnAreas");

            migrationBuilder.DropTable(
                name: "Realms");

            migrationBuilder.DropTable(
                name: "QuestDefinitions");

            migrationBuilder.DropTable(
                name: "NpcTemplates");

            migrationBuilder.DropTable(
                name: "Maps");

            migrationBuilder.DropTable(
                name: "QuestAreas");
        }
    }
}
