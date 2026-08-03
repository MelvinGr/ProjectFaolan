using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ClientSettingsBlobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "SubscriptionSettings",
                table: "Accounts",
                type: "BLOB",
                nullable: true);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterClientSettings");

            migrationBuilder.DropColumn(
                name: "SubscriptionSettings",
                table: "Accounts");
        }
    }
}
