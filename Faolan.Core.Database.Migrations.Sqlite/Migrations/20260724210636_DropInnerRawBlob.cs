using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DropInnerRawBlob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InnerRaw",
                table: "ConversationScreenParameters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InnerRaw",
                table: "ConversationScreenParameters",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");
        }
    }
}
