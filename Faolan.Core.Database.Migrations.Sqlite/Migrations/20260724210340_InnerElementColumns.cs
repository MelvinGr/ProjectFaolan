using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InnerElementColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "Category",
                table: "ConversationScreenParameters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentKey",
                table: "ConversationScreenParameters",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Flags",
                table: "ConversationScreenParameters",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "ConversationScreenParameters",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<uint>(
                name: "LocalizedId",
                table: "ConversationScreenParameters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalizedToken",
                table: "ConversationScreenParameters",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "ConversationScreenParameters",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConversationScreenParameter_Kind",
                table: "ConversationScreenParameters",
                sql: "Kind IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ConversationScreenParameter_Kind",
                table: "ConversationScreenParameters");

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
        }
    }
}
