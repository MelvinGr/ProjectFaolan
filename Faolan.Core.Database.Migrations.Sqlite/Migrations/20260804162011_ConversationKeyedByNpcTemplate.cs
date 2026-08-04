using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ConversationKeyedByNpcTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationNodes_NpcName",
                table: "ConversationNodes");

            migrationBuilder.DropIndex(
                name: "IX_ConversationEntries_NpcName",
                table: "ConversationEntries");

            migrationBuilder.AlterColumn<uint>(
                name: "NpcTemplateId",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u,
                oldClrType: typeof(uint),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<uint>(
                name: "NpcTemplateId",
                table: "ConversationEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u,
                oldClrType: typeof(uint),
                oldType: "INTEGER",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<uint>(
                name: "NpcTemplateId",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<uint>(
                name: "NpcTemplateId",
                table: "ConversationEntries",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(uint),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_NpcName",
                table: "ConversationNodes",
                column: "NpcName");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_NpcName",
                table: "ConversationEntries",
                column: "NpcName");
        }
    }
}
