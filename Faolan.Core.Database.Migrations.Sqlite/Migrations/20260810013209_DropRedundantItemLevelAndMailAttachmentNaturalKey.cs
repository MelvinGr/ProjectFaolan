using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DropRedundantItemLevelAndMailAttachmentNaturalKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_MailAttachments",
                table: "MailAttachments");

            migrationBuilder.DropIndex(
                name: "IX_MailAttachments_MailId",
                table: "MailAttachments");

            migrationBuilder.DropColumn(
                name: "ItemLevel",
                table: "TradepostListings");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "MailAttachments");

            migrationBuilder.DropColumn(
                name: "ItemLevel",
                table: "MailAttachments");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MailAttachments",
                table: "MailAttachments",
                columns: new[] { "MailId", "ItemInstanceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_MailAttachments",
                table: "MailAttachments");

            migrationBuilder.AddColumn<uint>(
                name: "ItemLevel",
                table: "TradepostListings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "Id",
                table: "MailAttachments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<uint>(
                name: "ItemLevel",
                table: "MailAttachments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddPrimaryKey(
                name: "PK_MailAttachments",
                table: "MailAttachments",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_MailAttachments_MailId",
                table: "MailAttachments",
                column: "MailId");
        }
    }
}
