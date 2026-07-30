using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ConversationNodeTriggerWorldObject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "TriggerWorldObjectId",
                table: "ConversationNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationNodes_TriggerWorldObjectId",
                table: "ConversationNodes",
                column: "TriggerWorldObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationNodes_WorldObjects_TriggerWorldObjectId",
                table: "ConversationNodes",
                column: "TriggerWorldObjectId",
                principalTable: "WorldObjects",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationNodes_WorldObjects_TriggerWorldObjectId",
                table: "ConversationNodes");

            migrationBuilder.DropIndex(
                name: "IX_ConversationNodes_TriggerWorldObjectId",
                table: "ConversationNodes");

            migrationBuilder.DropColumn(
                name: "TriggerWorldObjectId",
                table: "ConversationNodes");
        }
    }
}
