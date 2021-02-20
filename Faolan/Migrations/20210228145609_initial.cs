using Microsoft.EntityFrameworkCore.Migrations;

namespace Faolan.Migrations
{
    public partial class initial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                "Accounts",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>("TEXT", maxLength: 128, nullable: true),
                    Password = table.Column<string>("TEXT", maxLength: 128, nullable: true),
                    AuthStatus = table.Column<uint>("INTEGER", nullable: false),
                    ClientInstance = table.Column<uint>("INTEGER", nullable: false),
                    CreateCounter = table.Column<int>("INTEGER", nullable: false),
                    CreateState = table.Column<int>("INTEGER", nullable: false),
                    State = table.Column<byte>("INTEGER", nullable: false),
                    Type = table.Column<byte>("INTEGER", nullable: false),
                    LastConnection = table.Column<uint>("INTEGER", nullable: false),
                    CreationDate = table.Column<uint>("INTEGER", nullable: false),
                    LastIPAddress = table.Column<string>("TEXT", maxLength: 64, nullable: true)
                },
                constraints: table => { table.PrimaryKey("PK_Accounts", x => x.Id); });

            migrationBuilder.CreateTable(
                "Realms",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>("TEXT", maxLength: 128, nullable: true),
                    IpAddress = table.Column<string>("TEXT", maxLength: 64, nullable: true),
                    Port = table.Column<ushort>("INTEGER", nullable: false),
                    Country = table.Column<byte>("INTEGER", nullable: false),
                    FullStatus = table.Column<bool>("INTEGER", nullable: false),
                    LoadStatus = table.Column<byte>("INTEGER", nullable: false),
                    OnlineStatus = table.Column<byte>("INTEGER", nullable: false),
                    Type = table.Column<ushort>("INTEGER", nullable: false)
                },
                constraints: table => { table.PrimaryKey("PK_Realms", x => x.Id); });

            migrationBuilder.CreateTable(
                "Spells",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>("TEXT", maxLength: 128, nullable: true),
                    CastTime = table.Column<ushort>("INTEGER", nullable: false),
                    EffectSpell = table.Column<uint>("INTEGER", nullable: false),
                    Level = table.Column<byte>("INTEGER", nullable: false),
                    ManaCost = table.Column<ushort>("INTEGER", nullable: false),
                    MaxDamage = table.Column<ushort>("INTEGER", nullable: false),
                    MinDamage = table.Column<ushort>("INTEGER", nullable: false)
                },
                constraints: table => { table.PrimaryKey("PK_Spells", x => x.Id); });

            migrationBuilder.CreateTable(
                "WorldMaps",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>("TEXT", maxLength: 128, nullable: true),
                    PositionX = table.Column<float>("REAL", nullable: false),
                    PositionY = table.Column<float>("REAL", nullable: false),
                    PositionZ = table.Column<float>("REAL", nullable: false),
                    RotationX = table.Column<float>("REAL", nullable: false),
                    RotationY = table.Column<float>("REAL", nullable: false),
                    RotationZ = table.Column<float>("REAL", nullable: false)
                },
                constraints: table => { table.PrimaryKey("PK_WorldMaps", x => x.Id); });

            migrationBuilder.CreateTable(
                "WorldObjects",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Data0 = table.Column<uint>("INTEGER", nullable: false),
                    Data1 = table.Column<uint>("INTEGER", nullable: false),
                    Data2 = table.Column<uint>("INTEGER", nullable: false),
                    Data3 = table.Column<uint>("INTEGER", nullable: false),
                    Data4 = table.Column<uint>("INTEGER", nullable: false),
                    Data5 = table.Column<uint>("INTEGER", nullable: false),
                    Sdat1 = table.Column<string>("TEXT", nullable: true),
                    Sdat2 = table.Column<string>("TEXT", nullable: true),
                    Unk0 = table.Column<uint>("INTEGER", nullable: false),
                    Unk1 = table.Column<uint>("INTEGER", nullable: false),
                    PositionX = table.Column<float>("REAL", nullable: false),
                    PositionY = table.Column<float>("REAL", nullable: false),
                    PositionZ = table.Column<float>("REAL", nullable: false),
                    RotationX = table.Column<float>("REAL", nullable: false),
                    RotationY = table.Column<float>("REAL", nullable: false),
                    RotationZ = table.Column<float>("REAL", nullable: false)
                },
                constraints: table => { table.PrimaryKey("PK_WorldObjects", x => x.Id); });

            migrationBuilder.CreateTable(
                "Characters",
                table => new
                {
                    Id = table.Column<uint>("INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>("TEXT", maxLength: 128, nullable: true),
                    Class = table.Column<byte>("INTEGER", nullable: false),
                    HeadMesh = table.Column<uint>("INTEGER", nullable: false),
                    Level = table.Column<byte>("INTEGER", nullable: false),
                    Sex = table.Column<byte>("INTEGER", nullable: false),
                    Race = table.Column<byte>("INTEGER", nullable: false),
                    Size = table.Column<byte>("INTEGER", nullable: false),
                    SkinColor = table.Column<byte>("INTEGER", nullable: false),
                    Voice = table.Column<byte>("INTEGER", nullable: false),
                    Experience = table.Column<uint>("INTEGER", nullable: false),
                    Language = table.Column<string>("TEXT", maxLength: 2, nullable: true),
                    LastConnection = table.Column<uint>("INTEGER", nullable: false),
                    CreationDate = table.Column<uint>("INTEGER", nullable: false),
                    LastIPAddress = table.Column<string>("TEXT", maxLength: 64, nullable: true),
                    CurrentMapId = table.Column<uint>("INTEGER", nullable: false),
                    PositionX = table.Column<float>("REAL", nullable: false),
                    PositionY = table.Column<float>("REAL", nullable: false),
                    PositionZ = table.Column<float>("REAL", nullable: false),
                    RotationX = table.Column<float>("REAL", nullable: false),
                    RotationY = table.Column<float>("REAL", nullable: false),
                    RotationZ = table.Column<float>("REAL", nullable: false),
                    AccountId = table.Column<uint>("INTEGER", nullable: false),
                    RealmId = table.Column<uint>("INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                    table.ForeignKey(
                        "FK_Characters_Accounts_AccountId",
                        x => x.AccountId,
                        "Accounts",
                        "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        "FK_Characters_Realms_RealmId",
                        x => x.RealmId,
                        "Realms",
                        "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                "IX_Accounts_UserName",
                "Accounts",
                "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                "IX_Characters_AccountId",
                "Characters",
                "AccountId");

            migrationBuilder.CreateIndex(
                "IX_Characters_Name",
                "Characters",
                "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                "IX_Characters_RealmId",
                "Characters",
                "RealmId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                "Characters");

            migrationBuilder.DropTable(
                "Spells");

            migrationBuilder.DropTable(
                "WorldMaps");

            migrationBuilder.DropTable(
                "WorldObjects");

            migrationBuilder.DropTable(
                "Accounts");

            migrationBuilder.DropTable(
                "Realms");
        }
    }
}