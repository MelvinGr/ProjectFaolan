using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DialogueKeysAsDecryptionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayKey",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "FormatKey",
                table: "StockPhrases");

            migrationBuilder.DropColumn(
                name: "FormatKey",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "ConversationScreens");

            migrationBuilder.DropColumn(
                name: "ContentKey",
                table: "ConversationScreenParameters");

            migrationBuilder.AddColumn<uint>(
                name: "DisplayKey1",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "DisplayKey2",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "DisplayKey3",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "DisplayKey4",
                table: "WorldObjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey1",
                table: "StockPhrases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey2",
                table: "StockPhrases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey3",
                table: "StockPhrases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey4",
                table: "StockPhrases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey1",
                table: "NpcChatter",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey2",
                table: "NpcChatter",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey3",
                table: "NpcChatter",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "FormatKey4",
                table: "NpcChatter",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "Key1",
                table: "ConversationScreens",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "Key2",
                table: "ConversationScreens",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "Key3",
                table: "ConversationScreens",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "Key4",
                table: "ConversationScreens",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ContentKey1",
                table: "ConversationScreenParameters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ContentKey2",
                table: "ConversationScreenParameters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ContentKey3",
                table: "ConversationScreenParameters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ContentKey4",
                table: "ConversationScreenParameters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayKey1",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayKey2",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayKey3",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "DisplayKey4",
                table: "WorldObjects");

            migrationBuilder.DropColumn(
                name: "FormatKey1",
                table: "StockPhrases");

            migrationBuilder.DropColumn(
                name: "FormatKey2",
                table: "StockPhrases");

            migrationBuilder.DropColumn(
                name: "FormatKey3",
                table: "StockPhrases");

            migrationBuilder.DropColumn(
                name: "FormatKey4",
                table: "StockPhrases");

            migrationBuilder.DropColumn(
                name: "FormatKey1",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "FormatKey2",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "FormatKey3",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "FormatKey4",
                table: "NpcChatter");

            migrationBuilder.DropColumn(
                name: "Key1",
                table: "ConversationScreens");

            migrationBuilder.DropColumn(
                name: "Key2",
                table: "ConversationScreens");

            migrationBuilder.DropColumn(
                name: "Key3",
                table: "ConversationScreens");

            migrationBuilder.DropColumn(
                name: "Key4",
                table: "ConversationScreens");

            migrationBuilder.DropColumn(
                name: "ContentKey1",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "ContentKey2",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "ContentKey3",
                table: "ConversationScreenParameters");

            migrationBuilder.DropColumn(
                name: "ContentKey4",
                table: "ConversationScreenParameters");

            migrationBuilder.AddColumn<string>(
                name: "DisplayKey",
                table: "WorldObjects",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormatKey",
                table: "StockPhrases",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FormatKey",
                table: "NpcChatter",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "ConversationScreens",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContentKey",
                table: "ConversationScreenParameters",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }
    }
}
