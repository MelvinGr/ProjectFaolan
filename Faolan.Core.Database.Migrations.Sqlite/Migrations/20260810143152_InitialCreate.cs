using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Data0 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Slot = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Abilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    State = table.Column<byte>(type: "INTEGER", nullable: false),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false),
                    CreationDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastConnection = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClaimsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    GmGodMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    GmInvisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AuthChallenge = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastIpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AuthStatus = table.Column<uint>(type: "INTEGER", nullable: false),
                    SubscriptionSettings = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ClientInstance = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.CheckConstraint("CK_Account_State", "State IN (0, 1, 2)");
                    table.CheckConstraint("CK_Account_Type", "Type IN (0, 1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "Achievements",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Achievements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppearanceChoices",
                columns: table => new
                {
                    Race = table.Column<byte>(type: "INTEGER", nullable: false),
                    Sex = table.Column<byte>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Max = table.Column<int>(type: "INTEGER", nullable: false),
                    Min = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppearanceChoices", x => new { x.Race, x.Sex, x.Category, x.Sequence });
                    table.CheckConstraint("CK_AppearanceChoice_Race", "Race IN (1, 2, 3, 10)");
                    table.CheckConstraint("CK_AppearanceChoice_Sex", "Sex IN (0, 1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "AppearanceSexOptions",
                columns: table => new
                {
                    Sex = table.Column<byte>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Max = table.Column<int>(type: "INTEGER", nullable: false),
                    Min = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppearanceSexOptions", x => new { x.Sex, x.Category, x.Sequence });
                    table.CheckConstraint("CK_AppearanceSexOption_Sex", "Sex IN (0, 1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<uint>(type: "INTEGER", nullable: false),
                    SenderId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsWhisper = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelType = table.Column<byte>(type: "INTEGER", nullable: false),
                    SenderName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClassAttributeBases",
                columns: table => new
                {
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassAttributeBases", x => new { x.Class, x.Level, x.StatId });
                    table.CheckConstraint("CK_ClassAttributeBase_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                });

            migrationBuilder.CreateTable(
                name: "ClassComboScalings",
                columns: table => new
                {
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    DpsModifier = table.Column<uint>(type: "INTEGER", nullable: false),
                    SecondaryModifier = table.Column<uint>(type: "INTEGER", nullable: false),
                    StaminaDivisor = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassComboScalings", x => new { x.Class, x.Level });
                    table.CheckConstraint("CK_ClassComboScaling_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                });

            migrationBuilder.CreateTable(
                name: "Combos",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: true),
                    GameActionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Level = table.Column<byte>(type: "INTEGER", nullable: false),
                    Cooldown = table.Column<int>(type: "INTEGER", nullable: false),
                    Criteria = table.Column<int>(type: "INTEGER", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false),
                    Stamina = table.Column<int>(type: "INTEGER", nullable: false),
                    Actions = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Animation = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Combos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeatIdMaps",
                columns: table => new
                {
                    WireFeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RootFlag = table.Column<uint>(type: "INTEGER", nullable: false),
                    BaseIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    Rank = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatIdMaps", x => x.WireFeatId);
                });

            migrationBuilder.CreateTable(
                name: "Feats",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Flags = table.Column<uint>(type: "INTEGER", nullable: false),
                    TemplateCategory = table.Column<uint>(type: "INTEGER", nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Tree = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BaseName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Maps",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    InstanceMode = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayfieldInstanced = table.Column<bool>(type: "INTEGER", nullable: false),
                    PopulationCap = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassSpecific = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TravelConfirmKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TravelConfirmKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TravelConfirmKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TravelConfirmKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    PlayfieldFieldA = table.Column<uint>(type: "INTEGER", nullable: false),
                    PlayfieldCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    PlayfieldBlobLastWord = table.Column<uint>(type: "INTEGER", nullable: false),
                    PlayfieldFieldC = table.Column<uint>(type: "INTEGER", nullable: false),
                    PlayfieldFlags = table.Column<uint>(type: "INTEGER", nullable: false),
                    PlayfieldToken = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MorphTargets",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    Category = table.Column<byte>(type: "INTEGER", nullable: false),
                    Weight = table.Column<float>(type: "REAL", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MorphTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NpcTemplates",
                columns: table => new
                {
                    TemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    DeathBehaviorId = table.Column<uint>(type: "INTEGER", nullable: true),
                    DeathActivationTime = table.Column<uint>(type: "INTEGER", nullable: true),
                    LootContainerTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcTemplates", x => x.TemplateId);
                    table.CheckConstraint("CK_NpcTemplate_LootContainerTemplateId", "LootContainerTemplateId IN (13781, 13782)");
                });

            migrationBuilder.CreateTable(
                name: "QuestLines",
                columns: table => new
                {
                    QuestLineId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TitleRefId = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapRefId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionRefId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TitleKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TitleKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TitleKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TitleKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TitleCategory = table.Column<uint>(type: "INTEGER", nullable: false),
                    Magic = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapCategory = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionCategory = table.Column<uint>(type: "INTEGER", nullable: false),
                    TableField1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FirstQuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    LastQuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PrevQuestLineId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Tier = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestLines", x => x.QuestLineId);
                });

            migrationBuilder.CreateTable(
                name: "Realms",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Country = table.Column<byte>(type: "INTEGER", nullable: false),
                    LoadStatus = table.Column<byte>(type: "INTEGER", nullable: false),
                    OnlineStatus = table.Column<byte>(type: "INTEGER", nullable: false),
                    Type = table.Column<ushort>(type: "INTEGER", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ChatServerPort = table.Column<ushort>(type: "INTEGER", nullable: false),
                    CsPlayerAgentPort = table.Column<ushort>(type: "INTEGER", nullable: false),
                    Port = table.Column<ushort>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Realms", x => x.Id);
                    table.CheckConstraint("CK_Realm_Country", "Country IN (0, 1, 2, 3)");
                    table.CheckConstraint("CK_Realm_LoadStatus", "LoadStatus IN (0, 1, 2, 3, 4)");
                    table.CheckConstraint("CK_Realm_OnlineStatus", "OnlineStatus IN (0, 2)");
                    table.CheckConstraint("CK_Realm_Type", "Type IN (0, 1, 256, 257, 1081)");
                });

            migrationBuilder.CreateTable(
                name: "Recipes",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailingFlag = table.Column<byte>(type: "INTEGER", nullable: false),
                    Header1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    MidFlag = table.Column<uint>(type: "INTEGER", nullable: false),
                    RdbType = table.Column<uint>(type: "INTEGER", nullable: false),
                    RequiredFeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Tail = table.Column<uint>(type: "INTEGER", nullable: false),
                    RequiredWorkshopFeatId = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Spells",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    Level = table.Column<byte>(type: "INTEGER", nullable: false),
                    GuiStackingOrder = table.Column<byte>(type: "INTEGER", nullable: false),
                    GuiStackingLine = table.Column<uint>(type: "INTEGER", nullable: false),
                    ScalingAttribute = table.Column<uint>(type: "INTEGER", nullable: false),
                    SplashRadius = table.Column<float>(type: "REAL", nullable: false),
                    SplashDamage = table.Column<float>(type: "REAL", nullable: false),
                    SplashMaxTargets = table.Column<uint>(type: "INTEGER", nullable: false),
                    Range = table.Column<float>(type: "REAL", nullable: false),
                    DotDelta = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    DotType = table.Column<uint>(type: "INTEGER", nullable: false),
                    DotIntervalMs = table.Column<uint>(type: "INTEGER", nullable: false),
                    ChannelingIntervalMs = table.Column<uint>(type: "INTEGER", nullable: false),
                    CriticalChanceHundredths = table.Column<uint>(type: "INTEGER", nullable: false),
                    CriticalDamageHundredths = table.Column<uint>(type: "INTEGER", nullable: false),
                    AreaRadius = table.Column<float>(type: "REAL", nullable: false),
                    DurationMs = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectSpell = table.Column<uint>(type: "INTEGER", nullable: false),
                    SummonSpawnHash = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SummonIsCompanion = table.Column<bool>(type: "INTEGER", nullable: false),
                    SummonPetBuff = table.Column<uint>(type: "INTEGER", nullable: true),
                    SummonPointCost = table.Column<uint>(type: "INTEGER", nullable: true),
                    CastTime = table.Column<ushort>(type: "INTEGER", nullable: false),
                    RecastCentiseconds = table.Column<uint>(type: "INTEGER", nullable: false),
                    ManaCost = table.Column<ushort>(type: "INTEGER", nullable: false),
                    ManaCostIsPercent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spells", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockPhrases",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Category = table.Column<uint>(type: "INTEGER", nullable: false),
                    Knubot = table.Column<uint>(type: "INTEGER", nullable: false),
                    RemoteFormatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey4 = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockPhrases", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ClassAbilities",
                columns: table => new
                {
                    AbilityId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    Level = table.Column<byte>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassAbilities", x => new { x.Class, x.AbilityId });
                    table.CheckConstraint("CK_ClassAbility_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
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
                    ComboId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    Level = table.Column<byte>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassCombos", x => new { x.Class, x.ComboId });
                    table.CheckConstraint("CK_ClassCombo_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
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
                    WireFeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiresWireFeatId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "FeatComboAttackModifiers",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ComboId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AttackSlot = table.Column<uint>(type: "INTEGER", nullable: false),
                    DpsPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    CriticalChanceRating = table.Column<int>(type: "INTEGER", nullable: false),
                    HatePercent = table.Column<int>(type: "INTEGER", nullable: false),
                    DamageTypeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatComboAttackModifiers", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatComboAttackModifiers_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatComboModifiers",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ComboId = table.Column<uint>(type: "INTEGER", nullable: false),
                    DamagePercentHundredths = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownPercentHundredths = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatComboModifiers", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatComboModifiers_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatEffects",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<uint>(type: "INTEGER", nullable: false),
                    HighValue = table.Column<int>(type: "INTEGER", nullable: true),
                    LowLevel = table.Column<uint>(type: "INTEGER", nullable: true),
                    HighLevel = table.Column<uint>(type: "INTEGER", nullable: true)
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
                name: "FeatProcs",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ComboId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SpellLineId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SelfSpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    TargetSpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Trigger = table.Column<uint>(type: "INTEGER", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatProcs", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatProcs_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatSpellGrants",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatSpellGrants", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatSpellGrants_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatSpellModifiers",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SpellLineId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    PercentHundredths = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatSpellModifiers", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatSpellModifiers_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatSpellStatModifiers",
                columns: table => new
                {
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SpellLineId = table.Column<uint>(type: "INTEGER", nullable: true),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: true),
                    PercentHundredths = table.Column<int>(type: "INTEGER", nullable: true),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatSpellStatModifiers", x => new { x.FeatId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeatSpellStatModifiers_Feats_FeatId",
                        column: x => x.FeatId,
                        principalTable: "Feats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapLootEntries",
                columns: table => new
                {
                    ItemTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "MapRegions",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MinY = table.Column<float>(type: "REAL", nullable: false),
                    MaxY = table.Column<float>(type: "REAL", nullable: false),
                    MaxX = table.Column<float>(type: "REAL", nullable: false),
                    MaxZ = table.Column<float>(type: "REAL", nullable: false),
                    MinX = table.Column<float>(type: "REAL", nullable: false),
                    MinZ = table.Column<float>(type: "REAL", nullable: false),
                    ScryId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AreaNameId = table.Column<uint>(type: "INTEGER", nullable: true),
                    AreaNameSource = table.Column<int>(type: "INTEGER", nullable: true),
                    NoMount = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapRegions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapRegions_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorldObjects",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    DestinationMapId = table.Column<uint>(type: "INTEGER", nullable: true),
                    EffectQuestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    GateQuestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ObjectId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TemplateItemId = table.Column<uint>(type: "INTEGER", nullable: true),
                    InteractAction = table.Column<uint>(type: "INTEGER", nullable: false),
                    ShowPopupOnUse = table.Column<bool>(type: "INTEGER", nullable: true),
                    EffectDynelState = table.Column<byte>(type: "INTEGER", nullable: false),
                    GeometryAssetId = table.Column<uint>(type: "INTEGER", nullable: false),
                    GeometryAssetType = table.Column<uint>(type: "INTEGER", nullable: false),
                    GeometryName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BoundsMinX = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMinY = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMinZ = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMaxX = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMaxY = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMaxZ = table.Column<float>(type: "REAL", nullable: false),
                    GeometryWord0 = table.Column<uint>(type: "INTEGER", nullable: false),
                    GeometryWord1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    GeometryFlag = table.Column<bool>(type: "INTEGER", nullable: false),
                    DestinationX = table.Column<float>(type: "REAL", nullable: true),
                    DestinationY = table.Column<float>(type: "REAL", nullable: true),
                    DestinationZ = table.Column<float>(type: "REAL", nullable: true),
                    DisplayKind = table.Column<byte>(type: "INTEGER", nullable: true),
                    DisplayId = table.Column<uint>(type: "INTEGER", nullable: true),
                    DisplayToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DisplayCategory = table.Column<uint>(type: "INTEGER", nullable: true),
                    DisplayFlags = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    DisplayKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    DisplayKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    DisplayKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    DisplayKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    DisplayKnubot = table.Column<uint>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CarsGroup = table.Column<uint>(type: "INTEGER", nullable: false),
                    Kdtree = table.Column<uint>(type: "INTEGER", nullable: false),
                    SubOpcode = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestItemEffect = table.Column<uint>(type: "INTEGER", nullable: true),
                    WaypointType = table.Column<uint>(type: "INTEGER", nullable: true),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false),
                    RotationW = table.Column<float>(type: "REAL", nullable: false),
                    RotationX = table.Column<float>(type: "REAL", nullable: false),
                    RotationY = table.Column<float>(type: "REAL", nullable: false),
                    RotationZ = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldObjects", x => x.Id);
                    table.CheckConstraint("CK_WorldObject_DisplayKind", "DisplayKind IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_WorldObjects_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationNodes",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    ContentKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Discriminator = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    NpcName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "NpcAmbientAnimations",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AnimationId = table.Column<uint>(type: "INTEGER", nullable: false),
                    DurationCentiseconds = table.Column<uint>(type: "INTEGER", nullable: false),
                    Weight = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcAmbientAnimations", x => new { x.NpcTemplateId, x.AnimationId });
                    table.ForeignKey(
                        name: "FK_NpcAmbientAnimations_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcChatter",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SayInChat = table.Column<uint>(type: "INTEGER", nullable: false),
                    ChannelCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Category = table.Column<uint>(type: "INTEGER", nullable: false),
                    Knubot = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    FormatKey4 = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "NpcEncounters",
                columns: table => new
                {
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    LureNpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    BossNpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    BarkRadius = table.Column<float>(type: "REAL", nullable: false),
                    TriggerRadius = table.Column<float>(type: "REAL", nullable: false),
                    ResetSeconds = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcEncounters", x => new { x.MapId, x.LureNpcTemplateId });
                    table.ForeignKey(
                        name: "FK_NpcEncounters_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcEncounters_NpcTemplates_BossNpcTemplateId",
                        column: x => x.BossNpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcEncounters_NpcTemplates_LureNpcTemplateId",
                        column: x => x.LureNpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcFollowPairs",
                columns: table => new
                {
                    FollowerTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    LeaderTemplateId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcFollowPairs", x => new { x.FollowerTemplateId, x.LeaderTemplateId });
                    table.ForeignKey(
                        name: "FK_NpcFollowPairs_NpcTemplates_FollowerTemplateId",
                        column: x => x.FollowerTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcFollowPairs_NpcTemplates_LeaderTemplateId",
                        column: x => x.LeaderTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcMorphs",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    MorphId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<float>(type: "REAL", nullable: false),
                    Word2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    CommunityId = table.Column<uint>(type: "INTEGER", nullable: true),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: true),
                    RoamPathCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    BoundsMaxX = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMaxY = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMaxZ = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMinX = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMinY = table.Column<float>(type: "REAL", nullable: false),
                    BoundsMinZ = table.Column<float>(type: "REAL", nullable: false),
                    GroupOrdinal = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    GroupKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GroupName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
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
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    FlagA = table.Column<byte>(type: "INTEGER", nullable: false),
                    DescriptorSlot0 = table.Column<byte>(type: "INTEGER", nullable: false),
                    DescriptorSlot8 = table.Column<byte>(type: "INTEGER", nullable: false),
                    DescriptorSlot3 = table.Column<byte>(type: "INTEGER", nullable: false),
                    CastingLevel = table.Column<byte>(type: "INTEGER", nullable: false),
                    Duration = table.Column<ushort>(type: "INTEGER", nullable: false)
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
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    IirHeader = table.Column<uint>(type: "INTEGER", nullable: true),
                    Race = table.Column<uint>(type: "INTEGER", nullable: true),
                    Sex = table.Column<byte>(type: "INTEGER", nullable: true),
                    Side = table.Column<uint>(type: "INTEGER", nullable: true),
                    Flags = table.Column<uint>(type: "INTEGER", nullable: true),
                    HeadMesh = table.Column<uint>(type: "INTEGER", nullable: true),
                    MonsterData = table.Column<uint>(type: "INTEGER", nullable: true),
                    MonsterScale = table.Column<uint>(type: "INTEGER", nullable: true),
                    Radius = table.Column<int>(type: "INTEGER", nullable: true),
                    ChatId = table.Column<uint>(type: "INTEGER", nullable: true),
                    GuildId = table.Column<uint>(type: "INTEGER", nullable: true),
                    CarsGroup = table.Column<uint>(type: "INTEGER", nullable: true),
                    OrientationMode = table.Column<uint>(type: "INTEGER", nullable: true),
                    SoundScryScript = table.Column<uint>(type: "INTEGER", nullable: true),
                    ClientScryScript = table.Column<uint>(type: "INTEGER", nullable: true),
                    MountMesh = table.Column<uint>(type: "INTEGER", nullable: true),
                    Skulk = table.Column<uint>(type: "INTEGER", nullable: true),
                    MaxSkulk = table.Column<uint>(type: "INTEGER", nullable: true),
                    TradeskillFlags = table.Column<uint>(type: "INTEGER", nullable: true),
                    OocMovementMultiplier = table.Column<uint>(type: "INTEGER", nullable: true),
                    RunSpeed = table.Column<uint>(type: "INTEGER", nullable: true),
                    MaxRunSpeed = table.Column<uint>(type: "INTEGER", nullable: true),
                    Bcc = table.Column<uint>(type: "INTEGER", nullable: true),
                    BccState = table.Column<uint>(type: "INTEGER", nullable: true),
                    BloodType = table.Column<uint>(type: "INTEGER", nullable: true),
                    LooksPackage = table.Column<uint>(type: "INTEGER", nullable: true),
                    MountFlags = table.Column<uint>(type: "INTEGER", nullable: true),
                    MountScale = table.Column<uint>(type: "INTEGER", nullable: true),
                    MovementMultiplier = table.Column<uint>(type: "INTEGER", nullable: true),
                    VoiceSet = table.Column<uint>(type: "INTEGER", nullable: true),
                    ApprenticeLevel = table.Column<uint>(type: "INTEGER", nullable: true),
                    Level = table.Column<uint>(type: "INTEGER", nullable: true),
                    Health = table.Column<uint>(type: "INTEGER", nullable: true),
                    Life = table.Column<uint>(type: "INTEGER", nullable: true),
                    Mana = table.Column<uint>(type: "INTEGER", nullable: true),
                    MaxMana = table.Column<uint>(type: "INTEGER", nullable: true),
                    Stamina = table.Column<uint>(type: "INTEGER", nullable: true),
                    MaxStamina = table.Column<uint>(type: "INTEGER", nullable: true),
                    Community = table.Column<uint>(type: "INTEGER", nullable: true),
                    Disposition = table.Column<uint>(type: "INTEGER", nullable: true),
                    TemplateId = table.Column<uint>(type: "INTEGER", nullable: true),
                    NpcType = table.Column<uint>(type: "INTEGER", nullable: true),
                    NpcFlags = table.Column<uint>(type: "INTEGER", nullable: true),
                    Normalset = table.Column<uint>(type: "INTEGER", nullable: true),
                    SuperFamily = table.Column<uint>(type: "INTEGER", nullable: true),
                    ProfessionName = table.Column<uint>(type: "INTEGER", nullable: true),
                    ProfessionType = table.Column<uint>(type: "INTEGER", nullable: true),
                    SightSkill = table.Column<uint>(type: "INTEGER", nullable: true),
                    HearingSkill = table.Column<uint>(type: "INTEGER", nullable: true),
                    SmellingSkill = table.Column<uint>(type: "INTEGER", nullable: true),
                    SpawnAnimation = table.Column<uint>(type: "INTEGER", nullable: true),
                    LosHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    FactionId = table.Column<uint>(type: "INTEGER", nullable: true),
                    AnimActionId = table.Column<uint>(type: "INTEGER", nullable: true),
                    AnimActionTimer = table.Column<float>(type: "REAL", nullable: true),
                    AnimActionBlend = table.Column<bool>(type: "INTEGER", nullable: true),
                    AnimIdleId = table.Column<uint>(type: "INTEGER", nullable: true),
                    AnimIdleTimer = table.Column<float>(type: "REAL", nullable: true),
                    AnimIdleBlend = table.Column<bool>(type: "INTEGER", nullable: true),
                    AnimForcedId = table.Column<uint>(type: "INTEGER", nullable: true),
                    AnimForcedTimer = table.Column<float>(type: "REAL", nullable: true),
                    AnimForcedBlend = table.Column<bool>(type: "INTEGER", nullable: true),
                    AnimFlags = table.Column<uint>(type: "INTEGER", nullable: true),
                    CanFall = table.Column<bool>(type: "INTEGER", nullable: true),
                    MovementMode = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcStatCores", x => x.NpcTemplateId);
                    table.CheckConstraint("CK_NpcStatCore_Sex", "Sex IN (0, 1, 2, 3)");
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
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    VendorName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MaxSize = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    QuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestLineId = table.Column<uint>(type: "INTEGER", nullable: false),
                    GoalId = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: true),
                    NextQuestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasSnapshotRecord = table.Column<bool>(type: "INTEGER", nullable: false),
                    ObjectiveRegionVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    IndexQuestLineQuestIds = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IndexMagic = table.Column<uint>(type: "INTEGER", nullable: false),
                    IndexMarker = table.Column<uint>(type: "INTEGER", nullable: false),
                    IndexTableField1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    IndexTableField2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    IndexTableField3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    IndexTrailerWord = table.Column<uint>(type: "INTEGER", nullable: false),
                    JournalSequence = table.Column<uint>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    MarkerX = table.Column<uint>(type: "INTEGER", nullable: false),
                    MarkerY = table.Column<uint>(type: "INTEGER", nullable: false),
                    Field118 = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestLineFlags = table.Column<uint>(type: "INTEGER", nullable: false),
                    MoneyReward = table.Column<ulong>(type: "INTEGER", nullable: false),
                    XpReward = table.Column<uint>(type: "INTEGER", nullable: false),
                    ChainHeadQuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    FieldEc = table.Column<uint>(type: "INTEGER", nullable: false),
                    FieldF0 = table.Column<uint>(type: "INTEGER", nullable: false),
                    FieldF8 = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestDefinitions", x => x.QuestId);
                    table.ForeignKey(
                        name: "FK_QuestDefinitions_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuestDefinitions_QuestLines_QuestLineId",
                        column: x => x.QuestLineId,
                        principalTable: "QuestLines",
                        principalColumn: "QuestLineId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestLineRewards",
                columns: table => new
                {
                    QuestLineId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pos = table.Column<uint>(type: "INTEGER", nullable: false),
                    ItemTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RewardType = table.Column<uint>(type: "INTEGER", nullable: false),
                    Optional = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestLineRewards", x => new { x.QuestLineId, x.Pos });
                    table.ForeignKey(
                        name: "FK_QuestLineRewards_QuestLines_QuestLineId",
                        column: x => x.QuestLineId,
                        principalTable: "QuestLines",
                        principalColumn: "QuestLineId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Guilds",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RealmId = table.Column<uint>(type: "INTEGER", nullable: false),
                    CreationDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    GovernmentForm = table.Column<byte>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    MessageOfTheDay = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    Renown = table.Column<uint>(type: "INTEGER", nullable: false),
                    Cash = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RenownArtistry = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RenownGlory = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RenownValor = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Guilds", x => x.Id);
                    table.CheckConstraint("CK_Guild_GovernmentForm", "GovernmentForm IN (0, 1, 2, 3, 4, 5, 6)");
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
                    MailId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RealmId = table.Column<uint>(type: "INTEGER", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    BodyXml = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    RecipientName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SenderName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FooterWord = table.Column<uint>(type: "INTEGER", nullable: false),
                    RecipientClass = table.Column<uint>(type: "INTEGER", nullable: false),
                    SenderBankSlot = table.Column<uint>(type: "INTEGER", nullable: false),
                    SenderClass = table.Column<uint>(type: "INTEGER", nullable: false),
                    Timestamp1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Timestamp2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Money = table.Column<ulong>(type: "INTEGER", nullable: false)
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
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    IsStarter = table.Column<bool>(type: "INTEGER", nullable: false),
                    GrantLevel = table.Column<byte>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassSpells", x => new { x.Class, x.SpellId });
                    table.CheckConstraint("CK_ClassSpell_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TemplateId2 = table.Column<uint>(type: "INTEGER", nullable: true),
                    PairKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    PairKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    PairKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    PairKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 104),
                    ExcludeFromRandomLoot = table.Column<bool>(type: "INTEGER", nullable: false),
                    Level = table.Column<byte>(type: "INTEGER", nullable: false),
                    Flags = table.Column<uint>(type: "INTEGER", nullable: true),
                    MoreFlags = table.Column<uint>(type: "INTEGER", nullable: false),
                    Can = table.Column<uint>(type: "INTEGER", nullable: false),
                    GemSlots = table.Column<uint>(type: "INTEGER", nullable: false),
                    MaxDamage = table.Column<int>(type: "INTEGER", nullable: true),
                    MinDamage = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Hash = table.Column<uint>(type: "INTEGER", nullable: false),
                    SearchCategory = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<uint>(type: "INTEGER", nullable: false),
                    BagExtraSlots = table.Column<uint>(type: "INTEGER", nullable: true),
                    OnUseSpell = table.Column<uint>(type: "INTEGER", nullable: true),
                    OnUseLockSpellLine = table.Column<uint>(type: "INTEGER", nullable: true),
                    OnUseLockSeconds = table.Column<uint>(type: "INTEGER", nullable: true),
                    ResourceBagExtraSlots = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.CheckConstraint("CK_Item_Quality", "Quality IN (0, 1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_Item_Type", "Type IN (104, 112, 113)");
                    table.ForeignKey(
                        name: "FK_Items_Spells_OnUseSpell",
                        column: x => x.OnUseSpell,
                        principalTable: "Spells",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Mounts",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    CreatureId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mounts", x => x.Id);
                    table.CheckConstraint("CK_Mount_Quality", "Quality IN (0, 1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_Mounts_Spells_Id",
                        column: x => x.Id,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpellAreaEffects",
                columns: table => new
                {
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<uint>(type: "INTEGER", nullable: false),
                    Kind = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectSpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RadiusMetres = table.Column<uint>(type: "INTEGER", nullable: false),
                    MaxTargets = table.Column<uint>(type: "INTEGER", nullable: false),
                    PeriodCentiseconds = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellAreaEffects", x => new { x.SpellId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_SpellAreaEffects_Spells_SpellId",
                        column: x => x.SpellId,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpellEffects",
                columns: table => new
                {
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<byte>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<byte>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    TargetSpellId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Template = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Combat = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<uint>(type: "INTEGER", nullable: true),
                    MaxDurationMs = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellEffects", x => new { x.SpellId, x.Trigger, x.Sequence });
                    table.CheckConstraint("CK_SpellEffect_Kind", "Kind IN (0, 1)");
                    table.CheckConstraint("CK_SpellEffect_Trigger", "Trigger IN (0, 1)");
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
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<uint>(type: "INTEGER", nullable: false),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectKind = table.Column<uint>(type: "INTEGER", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAmount = table.Column<int>(type: "INTEGER", nullable: true),
                    PeriodCentiseconds = table.Column<uint>(type: "INTEGER", nullable: false),
                    HighAmount = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxHighAmount = table.Column<int>(type: "INTEGER", nullable: true),
                    LowLevel = table.Column<uint>(type: "INTEGER", nullable: true),
                    HighLevel = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpellStatEffects", x => new { x.SpellId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_SpellStatEffects_Spells_SpellId",
                        column: x => x.SpellId,
                        principalTable: "Spells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapHazards",
                columns: table => new
                {
                    RegionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    EffectSpellId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Damage = table.Column<uint>(type: "INTEGER", nullable: false),
                    Lethal = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapHazards", x => x.RegionId);
                    table.ForeignKey(
                        name: "FK_MapHazards_MapRegions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "MapRegions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapRegionVertices",
                columns: table => new
                {
                    RegionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    X = table.Column<float>(type: "REAL", nullable: false),
                    Z = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapRegionVertices", x => new { x.RegionId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_MapRegionVertices_MapRegions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "MapRegions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RespawnPoints",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AlwaysKnown = table.Column<bool>(type: "INTEGER", nullable: false),
                    Shady = table.Column<bool>(type: "INTEGER", nullable: false),
                    DiscoveryRegionId = table.Column<uint>(type: "INTEGER", nullable: true),
                    RestorationLevel = table.Column<float>(type: "REAL", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false),
                    RotationW = table.Column<float>(type: "REAL", nullable: false),
                    RotationX = table.Column<float>(type: "REAL", nullable: false),
                    RotationY = table.Column<float>(type: "REAL", nullable: false),
                    RotationZ = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RespawnPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RespawnPoints_MapRegions_DiscoveryRegionId",
                        column: x => x.DiscoveryRegionId,
                        principalTable: "MapRegions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RespawnPoints_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorldObjectWords",
                columns: table => new
                {
                    WorldObjectId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "ConversationEntries",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggerWorldObjectId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ConditionQuestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ConditionQuestState = table.Column<int>(type: "INTEGER", nullable: true),
                    RepeatGreeting = table.Column<bool>(type: "INTEGER", nullable: true),
                    NodeId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationEntries", x => x.Id);
                    table.CheckConstraint("CK_ConversationEntry_ConditionQuestState", "ConditionQuestState IN (0, 1)");
                    table.CheckConstraint("CK_ConversationEntry_Trigger", "Trigger IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_ConversationEntries_ConversationNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationEntries_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationEntries_WorldObjects_TriggerWorldObjectId",
                        column: x => x.TriggerWorldObjectId,
                        principalTable: "WorldObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ConversationPackets",
                columns: table => new
                {
                    ConversationNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SubOpcode = table.Column<uint>(type: "INTEGER", nullable: true),
                    Env = table.Column<byte>(type: "INTEGER", nullable: true),
                    Flag = table.Column<byte>(type: "INTEGER", nullable: true),
                    ShotSize = table.Column<byte>(type: "INTEGER", nullable: true),
                    ShotType = table.Column<byte>(type: "INTEGER", nullable: true),
                    ShotEmphasis = table.Column<byte>(type: "INTEGER", nullable: true),
                    ShotFov = table.Column<byte>(type: "INTEGER", nullable: true),
                    ShotTilt = table.Column<byte>(type: "INTEGER", nullable: true),
                    Autoface = table.Column<bool>(type: "INTEGER", nullable: true),
                    HeadPaddingY = table.Column<float>(type: "REAL", nullable: true),
                    Field = table.Column<uint>(type: "INTEGER", nullable: true),
                    SenderInstance = table.Column<uint>(type: "INTEGER", nullable: true)
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
                name: "TravelDestinations",
                columns: table => new
                {
                    FromNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    OptionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DestinationMapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Cost = table.Column<uint>(type: "INTEGER", nullable: true),
                    ArrivalX = table.Column<float>(type: "REAL", nullable: true),
                    ArrivalY = table.Column<float>(type: "REAL", nullable: true),
                    ArrivalZ = table.Column<float>(type: "REAL", nullable: true),
                    DelaySeconds = table.Column<float>(type: "REAL", nullable: false),
                    FromDialogueDirective = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelDestinations", x => new { x.FromNodeId, x.OptionId });
                    table.ForeignKey(
                        name: "FK_TravelDestinations_ConversationNodes_FromNodeId",
                        column: x => x.FromNodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TravelDestinations_Maps_DestinationMapId",
                        column: x => x.DestinationMapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcChatterParameter",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SayInChat = table.Column<uint>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcChatterParameter", x => new { x.NpcTemplateId, x.Channel, x.SayInChat, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_NpcChatterParameter_NpcChatter_NpcTemplateId_Channel_SayInChat",
                        columns: x => new { x.NpcTemplateId, x.Channel, x.SayInChat },
                        principalTable: "NpcChatter",
                        principalColumns: new[] { "NpcTemplateId", "Channel", "SayInChat" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpawnAreaPoints",
                columns: table => new
                {
                    NpcSpawnAreaId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PointIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false)
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<uint>(type: "INTEGER", nullable: true),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ClassId = table.Column<byte>(type: "INTEGER", nullable: true),
                    NpcSpawnAreaId = table.Column<uint>(type: "INTEGER", nullable: true),
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PlacementId = table.Column<uint>(type: "INTEGER", nullable: true),
                    HasEquipment = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprenticeLevel = table.Column<uint>(type: "INTEGER", nullable: false),
                    HeadMesh = table.Column<uint>(type: "INTEGER", nullable: false),
                    Health = table.Column<uint>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    Life = table.Column<uint>(type: "INTEGER", nullable: false),
                    Mana = table.Column<uint>(type: "INTEGER", nullable: false),
                    MaxMana = table.Column<uint>(type: "INTEGER", nullable: false),
                    MaxStamina = table.Column<uint>(type: "INTEGER", nullable: false),
                    MonsterData = table.Column<uint>(type: "INTEGER", nullable: false),
                    MonsterScale = table.Column<uint>(type: "INTEGER", nullable: false),
                    Stamina = table.Column<uint>(type: "INTEGER", nullable: false),
                    VoiceSet = table.Column<uint>(type: "INTEGER", nullable: false),
                    HearingSkill = table.Column<uint>(type: "INTEGER", nullable: true),
                    SightSkill = table.Column<uint>(type: "INTEGER", nullable: true),
                    SmellingSkill = table.Column<uint>(type: "INTEGER", nullable: true),
                    WireHealth = table.Column<uint>(type: "INTEGER", nullable: true),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false),
                    RotationW = table.Column<float>(type: "REAL", nullable: false),
                    RotationX = table.Column<float>(type: "REAL", nullable: false),
                    RotationY = table.Column<float>(type: "REAL", nullable: false),
                    RotationZ = table.Column<float>(type: "REAL", nullable: false)
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
                name: "ConversationEdges",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    FromNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    OptionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ToNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ConditionQuestId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ConditionQuestState = table.Column<int>(type: "INTEGER", nullable: true),
                    GrantQuestId = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationEdges", x => x.Id);
                    table.CheckConstraint("CK_ConversationEdge_ConditionQuestState", "ConditionQuestState IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_ConversationEdges_ConversationNodes_FromNodeId",
                        column: x => x.FromNodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationEdges_ConversationNodes_ToNodeId",
                        column: x => x.ToNodeId,
                        principalTable: "ConversationNodes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConversationEdges_QuestDefinitions_GrantQuestId",
                        column: x => x.GrantQuestId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "QuestTextRefs",
                columns: table => new
                {
                    QuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    RefId = table.Column<uint>(type: "INTEGER", nullable: false),
                    IsLocalized = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeyText1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    KeyText2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    KeyText3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    KeyText4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Flags = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Category = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "AccountClaims",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ItemTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    ClaimType = table.Column<uint>(type: "INTEGER", nullable: false),
                    MaxCharges = table.Column<uint>(type: "INTEGER", nullable: true),
                    Quantity = table.Column<uint>(type: "INTEGER", nullable: true),
                    Level = table.Column<ulong>(type: "INTEGER", nullable: false)
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
                    ItemId = table.Column<uint>(type: "INTEGER", nullable: false),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemStatModifiers", x => new { x.ItemId, x.StatId });
                    table.ForeignKey(
                        name: "FK_ItemStatModifiers_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcEquipment",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pos = table.Column<uint>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    Template = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    GoalId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Phase = table.Column<uint>(type: "INTEGER", nullable: false),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ObjectiveRefId = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestDefinitionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestItemId = table.Column<uint>(type: "INTEGER", nullable: true),
                    SubId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailingRefId = table.Column<uint>(type: "INTEGER", nullable: false),
                    GoalType = table.Column<int>(type: "INTEGER", nullable: false),
                    Partial = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrailingLocalized = table.Column<bool>(type: "INTEGER", nullable: false),
                    ObjectiveKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    ObjectiveKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    ObjectiveKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    ObjectiveKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    ObjectiveToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TargetName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TargetTemplateId = table.Column<uint>(type: "INTEGER", nullable: true),
                    GatesTargetVisibility = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiredLevel = table.Column<uint>(type: "INTEGER", nullable: true),
                    RequiredRankTitle = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RequiredRank = table.Column<uint>(type: "INTEGER", nullable: true),
                    TrailingFlags = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TrailingKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailingKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailingKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailingKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailingToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Extra1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Extra2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Extra3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Extra4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Extra5 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Extra6 = table.Column<uint>(type: "INTEGER", nullable: false),
                    GoalIdEcho = table.Column<uint>(type: "INTEGER", nullable: false),
                    ObjectiveCategory = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionOrder = table.Column<uint>(type: "INTEGER", nullable: false),
                    RequiredCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    Separator = table.Column<uint>(type: "INTEGER", nullable: true),
                    Sequence = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailingCategory = table.Column<uint>(type: "INTEGER", nullable: false),
                    Unknown1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Unknown2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    MarkerX = table.Column<uint>(type: "INTEGER", nullable: true),
                    MarkerY = table.Column<uint>(type: "INTEGER", nullable: true),
                    RegionId = table.Column<uint>(type: "INTEGER", nullable: true)
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
                        name: "FK_QuestGoalDefinitions_MapRegions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "MapRegions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuestGoalDefinitions_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuestGoalDefinitions_NpcTemplates_TargetTemplateId",
                        column: x => x.TargetTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId");
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
                    RecipeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Role = table.Column<byte>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Flag = table.Column<uint>(type: "INTEGER", nullable: false),
                    Trailing = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeItems", x => new { x.RecipeId, x.Role, x.Position });
                    table.CheckConstraint("CK_RecipeItem_Role", "Role IN (0, 1)");
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
                    ShopId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pos = table.Column<uint>(type: "INTEGER", nullable: false),
                    ItemTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Count = table.Column<uint>(type: "INTEGER", nullable: false),
                    Stat356 = table.Column<uint>(type: "INTEGER", nullable: false),
                    TailLevel = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RealmId = table.Column<uint>(type: "INTEGER", nullable: false),
                    SaleId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SellerName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BankSlot = table.Column<uint>(type: "INTEGER", nullable: false),
                    EntryFlags = table.Column<uint>(type: "INTEGER", nullable: false),
                    Handle = table.Column<uint>(type: "INTEGER", nullable: false),
                    ItemHandle = table.Column<uint>(type: "INTEGER", nullable: false),
                    ItemHash = table.Column<uint>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<uint>(type: "INTEGER", nullable: false),
                    Template = table.Column<uint>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<uint>(type: "INTEGER", nullable: false),
                    InstanceField = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Price = table.Column<ulong>(type: "INTEGER", nullable: false)
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
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<uint>(type: "INTEGER", nullable: false),
                    BindRespawnPointId = table.Column<uint>(type: "INTEGER", nullable: true),
                    GuildId = table.Column<uint>(type: "INTEGER", nullable: true),
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    MountId = table.Column<uint>(type: "INTEGER", nullable: true),
                    RealmId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    CreationDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastConnection = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    GuildRank = table.Column<byte>(type: "INTEGER", nullable: true),
                    Race = table.Column<byte>(type: "INTEGER", nullable: false),
                    Sex = table.Column<byte>(type: "INTEGER", nullable: false),
                    ActiveWeaponSet = table.Column<int>(type: "INTEGER", nullable: false),
                    VanityShownMask = table.Column<uint>(type: "INTEGER", nullable: false),
                    IsDead = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthChallenge = table.Column<Guid>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    LastIpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Experience = table.Column<uint>(type: "INTEGER", nullable: false),
                    Money = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Level = table.Column<ushort>(type: "INTEGER", nullable: false),
                    Kills = table.Column<uint>(type: "INTEGER", nullable: false),
                    Deaths = table.Column<uint>(type: "INTEGER", nullable: false),
                    MurderPoints = table.Column<uint>(type: "INTEGER", nullable: false),
                    ChosenGod = table.Column<uint>(type: "INTEGER", nullable: false),
                    CriminalUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TutorialStepsSeen = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false),
                    RotationW = table.Column<float>(type: "REAL", nullable: false),
                    RotationX = table.Column<float>(type: "REAL", nullable: false),
                    RotationY = table.Column<float>(type: "REAL", nullable: false),
                    RotationZ = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                    table.CheckConstraint("CK_Character_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                    table.CheckConstraint("CK_Character_GuildRank", "GuildRank IN (0, 4)");
                    table.CheckConstraint("CK_Character_Race", "Race IN (1, 2, 3, 10)");
                    table.CheckConstraint("CK_Character_Sex", "Sex IN (0, 1, 2, 3)");
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
                name: "ConversationScreens",
                columns: table => new
                {
                    ConversationNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PacketSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ScreenId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Key4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Category = table.Column<uint>(type: "INTEGER", nullable: false),
                    Knubot = table.Column<uint>(type: "INTEGER", nullable: false),
                    Antitag = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationScreens", x => new { x.ConversationNodeId, x.PacketSequence, x.Sequence });
                    table.ForeignKey(
                        name: "FK_ConversationScreens_ConversationPackets_ConversationNodeId_PacketSequence",
                        columns: x => new { x.ConversationNodeId, x.PacketSequence },
                        principalTable: "ConversationPackets",
                        principalColumns: new[] { "ConversationNodeId", "Sequence" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NpcSpawnEquipment",
                columns: table => new
                {
                    NpcSpawnId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Pos = table.Column<uint>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    Template = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    NpcSpawnId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    MorphId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<float>(type: "REAL", nullable: false),
                    Word2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
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
                    OrdinalPath = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NpcSpawnId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ParentPath = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Mode = table.Column<byte>(type: "INTEGER", nullable: false),
                    Flag = table.Column<byte>(type: "INTEGER", nullable: true),
                    HeaderFlags = table.Column<byte>(type: "INTEGER", nullable: true),
                    ListMin = table.Column<byte>(type: "INTEGER", nullable: true),
                    PathFlags = table.Column<byte>(type: "INTEGER", nullable: true),
                    TrailerByte = table.Column<byte>(type: "INTEGER", nullable: true),
                    ApproachFlag = table.Column<byte>(type: "INTEGER", nullable: true),
                    AnchorX = table.Column<float>(type: "REAL", nullable: true),
                    AnchorY = table.Column<float>(type: "REAL", nullable: true),
                    AnchorZ = table.Column<float>(type: "REAL", nullable: true),
                    Duration = table.Column<float>(type: "REAL", nullable: true),
                    Remaining = table.Column<float>(type: "REAL", nullable: true),
                    Speed = table.Column<float>(type: "REAL", nullable: true),
                    PathDirection1 = table.Column<float>(type: "REAL", nullable: true),
                    PathDirection2 = table.Column<float>(type: "REAL", nullable: true),
                    PathTrailerWord = table.Column<uint>(type: "INTEGER", nullable: true),
                    TargetX = table.Column<float>(type: "REAL", nullable: true),
                    TargetY = table.Column<float>(type: "REAL", nullable: true),
                    TargetZ = table.Column<float>(type: "REAL", nullable: true),
                    TargetRotationX = table.Column<float>(type: "REAL", nullable: true),
                    TargetRotationY = table.Column<float>(type: "REAL", nullable: true),
                    TargetRotationZ = table.Column<float>(type: "REAL", nullable: true),
                    TargetRotationW = table.Column<float>(type: "REAL", nullable: true),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    TerritoryIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    TerritoryPathIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    RotateTail = table.Column<int>(type: "INTEGER", nullable: true),
                    ApproachTarget = table.Column<uint>(type: "INTEGER", nullable: true),
                    ApproachType = table.Column<uint>(type: "INTEGER", nullable: true),
                    PathFloat10 = table.Column<uint>(type: "INTEGER", nullable: true),
                    PathFloat20 = table.Column<uint>(type: "INTEGER", nullable: true)
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
                name: "NpcAmbushes",
                columns: table => new
                {
                    MapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestDefinitionId = table.Column<uint>(type: "INTEGER", nullable: false),
                    GoalId = table.Column<uint>(type: "INTEGER", nullable: false),
                    DelaySeconds = table.Column<float>(type: "REAL", nullable: false),
                    AnchorDistance = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcAmbushes", x => new { x.MapId, x.NpcTemplateId });
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_NpcTemplates_NpcTemplateId",
                        column: x => x.NpcTemplateId,
                        principalTable: "NpcTemplates",
                        principalColumn: "TemplateId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_QuestDefinitions_QuestDefinitionId",
                        column: x => x.QuestDefinitionId,
                        principalTable: "QuestDefinitions",
                        principalColumn: "QuestId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NpcAmbushes_QuestGoalDefinitions_GoalId",
                        column: x => x.GoalId,
                        principalTable: "QuestGoalDefinitions",
                        principalColumn: "GoalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterAbilities",
                columns: table => new
                {
                    AbilityId = table.Column<uint>(type: "INTEGER", nullable: false),
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Size = table.Column<byte>(type: "INTEGER", nullable: false),
                    SkinColor = table.Column<byte>(type: "INTEGER", nullable: false),
                    Voice = table.Column<byte>(type: "INTEGER", nullable: false),
                    Arm = table.Column<float>(type: "REAL", nullable: false),
                    Arse = table.Column<float>(type: "REAL", nullable: false),
                    Bosom = table.Column<float>(type: "REAL", nullable: false),
                    CheekDepth = table.Column<float>(type: "REAL", nullable: false),
                    CheekHeight = table.Column<float>(type: "REAL", nullable: false),
                    CheekWidth = table.Column<float>(type: "REAL", nullable: false),
                    Chest = table.Column<float>(type: "REAL", nullable: false),
                    ChinLength = table.Column<float>(type: "REAL", nullable: false),
                    ChinWidth = table.Column<float>(type: "REAL", nullable: false),
                    CrookedNose = table.Column<float>(type: "REAL", nullable: false),
                    EarAngle = table.Column<float>(type: "REAL", nullable: false),
                    Ears = table.Column<float>(type: "REAL", nullable: false),
                    EyebrowScale = table.Column<float>(type: "REAL", nullable: false),
                    EyesAngle = table.Column<float>(type: "REAL", nullable: false),
                    EyesDepth = table.Column<float>(type: "REAL", nullable: false),
                    EyesHorizontalPos = table.Column<float>(type: "REAL", nullable: false),
                    EyesVerticalPos = table.Column<float>(type: "REAL", nullable: false),
                    FaceLength = table.Column<float>(type: "REAL", nullable: false),
                    JawWidth = table.Column<float>(type: "REAL", nullable: false),
                    Leg = table.Column<float>(type: "REAL", nullable: false),
                    MouthVerticalPos = table.Column<float>(type: "REAL", nullable: false),
                    MouthWidth = table.Column<float>(type: "REAL", nullable: false),
                    NoseAngle = table.Column<float>(type: "REAL", nullable: false),
                    NoseCurve = table.Column<float>(type: "REAL", nullable: false),
                    NoseLength = table.Column<float>(type: "REAL", nullable: false),
                    NoseWidth = table.Column<float>(type: "REAL", nullable: false),
                    Stomach = table.Column<float>(type: "REAL", nullable: false),
                    Thigh = table.Column<float>(type: "REAL", nullable: false),
                    HeadMesh = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
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
                    BuffId = table.Column<uint>(type: "INTEGER", nullable: false),
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
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
                name: "CharacterClientSettings",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Settings = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Preview = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterClientSettings", x => x.CharacterId);
                    table.ForeignKey(
                        name: "FK_CharacterClientSettings_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCombos",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ComboId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "CharacterFeatSpecs",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
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
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    FriendCharacterId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "CharacterQuestGoals",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    GoalId = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Progress = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "CharacterQuests",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuestId = table.Column<uint>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    GoalTargetName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    GoalProgress = table.Column<uint>(type: "INTEGER", nullable: false),
                    GoalRequired = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterQuests", x => x.Id);
                    table.CheckConstraint("CK_CharacterQuest_State", "State IN (0, 1)");
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    RespawnPointId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PairId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ListIndex = table.Column<byte>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false)
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
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Slot = table.Column<uint>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ShortcutType = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Rank = table.Column<byte>(type: "INTEGER", nullable: false)
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
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    SpellId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Value = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TagId = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "ItemInstances",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Container = table.Column<uint>(type: "INTEGER", nullable: false),
                    Count = table.Column<uint>(type: "INTEGER", nullable: false),
                    Bound = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatorName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OwnerCharacterId = table.Column<uint>(type: "INTEGER", nullable: true),
                    OwnerGuildId = table.Column<uint>(type: "INTEGER", nullable: true),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemInstances", x => x.Id);
                    table.CheckConstraint("CK_ItemInstance_Container", "Container IN (101, 104, 105, 109, 111, 112, 113, 114, 115)");
                    table.CheckConstraint("CK_ItemInstance_OneOwner", "(OwnerCharacterId IS NULL) <> (OwnerGuildId IS NULL)");
                    table.ForeignKey(
                        name: "FK_ItemInstances_Characters_OwnerCharacterId",
                        column: x => x.OwnerCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemInstances_Guilds_OwnerGuildId",
                        column: x => x.OwnerGuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemInstances_Items_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationScreenParameters",
                columns: table => new
                {
                    ConversationNodeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    PacketSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ScreenSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    LocalizedId = table.Column<uint>(type: "INTEGER", nullable: true),
                    LocalizedToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Category = table.Column<uint>(type: "INTEGER", nullable: true),
                    Flags = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ContentKey1 = table.Column<uint>(type: "INTEGER", nullable: false),
                    ContentKey2 = table.Column<uint>(type: "INTEGER", nullable: false),
                    ContentKey3 = table.Column<uint>(type: "INTEGER", nullable: false),
                    ContentKey4 = table.Column<uint>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationScreenParameters", x => new { x.ConversationNodeId, x.PacketSequence, x.ScreenSequence, x.Sequence });
                    table.CheckConstraint("CK_ConversationScreenParameter_Kind", "Kind IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_ConversationScreenParameters_ConversationScreens_ConversationNodeId_PacketSequence_ScreenSequence",
                        columns: x => new { x.ConversationNodeId, x.PacketSequence, x.ScreenSequence },
                        principalTable: "ConversationScreens",
                        principalColumns: new[] { "ConversationNodeId", "PacketSequence", "Sequence" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolNodeWaypoints",
                columns: table => new
                {
                    NpcSpawnId = table.Column<uint>(type: "INTEGER", nullable: false),
                    OrdinalPath = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false)
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
                    NpcSpawnId = table.Column<uint>(type: "INTEGER", nullable: false),
                    OrdinalPath = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<uint>(type: "INTEGER", nullable: false)
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
                name: "CharacterFeats",
                columns: table => new
                {
                    CharacterId = table.Column<uint>(type: "INTEGER", nullable: false),
                    SpecIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "MailAttachments",
                columns: table => new
                {
                    ItemInstanceId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MailId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    EscrowedInstanceId = table.Column<uint>(type: "INTEGER", nullable: true),
                    ItemHash = table.Column<uint>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<uint>(type: "INTEGER", nullable: false),
                    Template = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailerClass = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailerTail = table.Column<uint>(type: "INTEGER", nullable: false),
                    TrailerWord = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAttachments", x => new { x.MailId, x.ItemInstanceId });
                    table.ForeignKey(
                        name: "FK_MailAttachments_ItemInstances_EscrowedInstanceId",
                        column: x => x.EscrowedInstanceId,
                        principalTable: "ItemInstances",
                        principalColumn: "Id");
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
                name: "IX_CharacterFriends_FriendCharacterId",
                table: "CharacterFriends",
                column: "FriendCharacterId");

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
                name: "IX_ConversationEdges_FromNodeId",
                table: "ConversationEdges",
                column: "FromNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEdges_GrantQuestId",
                table: "ConversationEdges",
                column: "GrantQuestId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEdges_ToNodeId",
                table: "ConversationEdges",
                column: "ToNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_NodeId",
                table: "ConversationEntries",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_NpcTemplateId",
                table: "ConversationEntries",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_TriggerWorldObjectId",
                table: "ConversationEntries",
                column: "TriggerWorldObjectId");

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
                name: "IX_ItemInstances_OwnerCharacterId_Container_Slot",
                table: "ItemInstances",
                columns: new[] { "OwnerCharacterId", "Container", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemInstances_OwnerGuildId_Container_Slot",
                table: "ItemInstances",
                columns: new[] { "OwnerGuildId", "Container", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemInstances_TemplateId",
                table: "ItemInstances",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemInstanceSockets_GemItemId",
                table: "ItemInstanceSockets",
                column: "GemItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_OnUseSpell",
                table: "Items",
                column: "OnUseSpell");

            migrationBuilder.CreateIndex(
                name: "IX_MailAttachments_EscrowedInstanceId",
                table: "MailAttachments",
                column: "EscrowedInstanceId");

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
                name: "IX_MapRegions_MapId_Name",
                table: "MapRegions",
                columns: new[] { "MapId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MorphTargets_Name",
                table: "MorphTargets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NpcAmbushes_GoalId",
                table: "NpcAmbushes",
                column: "GoalId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcAmbushes_NpcTemplateId",
                table: "NpcAmbushes",
                column: "NpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcAmbushes_QuestDefinitionId",
                table: "NpcAmbushes",
                column: "QuestDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcEncounters_BossNpcTemplateId",
                table: "NpcEncounters",
                column: "BossNpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcEncounters_LureNpcTemplateId",
                table: "NpcEncounters",
                column: "LureNpcTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_NpcEquipment_Template",
                table: "NpcEquipment",
                column: "Template");

            migrationBuilder.CreateIndex(
                name: "IX_NpcFollowPairs_LeaderTemplateId",
                table: "NpcFollowPairs",
                column: "LeaderTemplateId");

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
                name: "IX_NpcSpawns_MapId_PositionX_PositionY_PositionZ_ClassId",
                table: "NpcSpawns",
                columns: new[] { "MapId", "PositionX", "PositionY", "PositionZ", "ClassId" },
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
                name: "IX_QuestDefinitions_MapId",
                table: "QuestDefinitions",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestDefinitions_QuestLineId",
                table: "QuestDefinitions",
                column: "QuestLineId");

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
                name: "IX_QuestGoalDefinitions_RegionId",
                table: "QuestGoalDefinitions",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestGoalDefinitions_TargetTemplateId",
                table: "QuestGoalDefinitions",
                column: "TargetTemplateId");

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
                name: "IX_RespawnPoints_DiscoveryRegionId",
                table: "RespawnPoints",
                column: "DiscoveryRegionId");

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
                name: "IX_TravelDestinations_DestinationMapId",
                table: "TravelDestinations",
                column: "DestinationMapId");

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
                name: "Achievements");

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
                name: "CharacterClientSettings");

            migrationBuilder.DropTable(
                name: "CharacterCombos");

            migrationBuilder.DropTable(
                name: "CharacterFeats");

            migrationBuilder.DropTable(
                name: "CharacterFriends");

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
                name: "ConversationEdges");

            migrationBuilder.DropTable(
                name: "ConversationEntries");

            migrationBuilder.DropTable(
                name: "ConversationScreenParameters");

            migrationBuilder.DropTable(
                name: "FeatComboAttackModifiers");

            migrationBuilder.DropTable(
                name: "FeatComboModifiers");

            migrationBuilder.DropTable(
                name: "FeatEffects");

            migrationBuilder.DropTable(
                name: "FeatPrereqs");

            migrationBuilder.DropTable(
                name: "FeatProcs");

            migrationBuilder.DropTable(
                name: "FeatSpellGrants");

            migrationBuilder.DropTable(
                name: "FeatSpellModifiers");

            migrationBuilder.DropTable(
                name: "FeatSpellStatModifiers");

            migrationBuilder.DropTable(
                name: "ItemInstanceSockets");

            migrationBuilder.DropTable(
                name: "ItemStatModifiers");

            migrationBuilder.DropTable(
                name: "MailAttachments");

            migrationBuilder.DropTable(
                name: "MapHazards");

            migrationBuilder.DropTable(
                name: "MapLootEntries");

            migrationBuilder.DropTable(
                name: "MapRegionVertices");

            migrationBuilder.DropTable(
                name: "MorphTargets");

            migrationBuilder.DropTable(
                name: "Mounts");

            migrationBuilder.DropTable(
                name: "NpcAmbientAnimations");

            migrationBuilder.DropTable(
                name: "NpcAmbushes");

            migrationBuilder.DropTable(
                name: "NpcChatterParameter");

            migrationBuilder.DropTable(
                name: "NpcEncounters");

            migrationBuilder.DropTable(
                name: "NpcEquipment");

            migrationBuilder.DropTable(
                name: "NpcFollowPairs");

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
                name: "QuestLineRewards");

            migrationBuilder.DropTable(
                name: "QuestTextRefs");

            migrationBuilder.DropTable(
                name: "RecipeItems");

            migrationBuilder.DropTable(
                name: "ShopItems");

            migrationBuilder.DropTable(
                name: "SpellAreaEffects");

            migrationBuilder.DropTable(
                name: "SpellEffects");

            migrationBuilder.DropTable(
                name: "SpellStatEffects");

            migrationBuilder.DropTable(
                name: "StockPhrases");

            migrationBuilder.DropTable(
                name: "TradepostListings");

            migrationBuilder.DropTable(
                name: "TravelDestinations");

            migrationBuilder.DropTable(
                name: "WorldObjectWords");

            migrationBuilder.DropTable(
                name: "CharacterFeatSpecs");

            migrationBuilder.DropTable(
                name: "Abilities");

            migrationBuilder.DropTable(
                name: "Combos");

            migrationBuilder.DropTable(
                name: "ConversationScreens");

            migrationBuilder.DropTable(
                name: "FeatIdMaps");

            migrationBuilder.DropTable(
                name: "Feats");

            migrationBuilder.DropTable(
                name: "ItemInstances");

            migrationBuilder.DropTable(
                name: "Mails");

            migrationBuilder.DropTable(
                name: "QuestGoalDefinitions");

            migrationBuilder.DropTable(
                name: "NpcChatter");

            migrationBuilder.DropTable(
                name: "PatrolNodes");

            migrationBuilder.DropTable(
                name: "Recipes");

            migrationBuilder.DropTable(
                name: "Shops");

            migrationBuilder.DropTable(
                name: "WorldObjects");

            migrationBuilder.DropTable(
                name: "ConversationPackets");

            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "QuestDefinitions");

            migrationBuilder.DropTable(
                name: "NpcSpawns");

            migrationBuilder.DropTable(
                name: "ConversationNodes");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Guilds");

            migrationBuilder.DropTable(
                name: "RespawnPoints");

            migrationBuilder.DropTable(
                name: "Spells");

            migrationBuilder.DropTable(
                name: "QuestLines");

            migrationBuilder.DropTable(
                name: "NpcSpawnAreas");

            migrationBuilder.DropTable(
                name: "Realms");

            migrationBuilder.DropTable(
                name: "MapRegions");

            migrationBuilder.DropTable(
                name: "NpcTemplates");

            migrationBuilder.DropTable(
                name: "Maps");
        }
    }
}
