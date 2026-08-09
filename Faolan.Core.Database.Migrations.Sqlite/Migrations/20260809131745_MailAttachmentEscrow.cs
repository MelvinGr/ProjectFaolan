using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MailAttachmentEscrow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances");

            migrationBuilder.AddColumn<uint>(
                name: "EscrowedInstanceId",
                table: "MailAttachments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailAttachments_EscrowedInstanceId",
                table: "MailAttachments",
                column: "EscrowedInstanceId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances",
                sql: "Container IN (101, 104, 105, 109, 111, 112, 113, 114, 115)");

            migrationBuilder.AddForeignKey(
                name: "FK_MailAttachments_ItemInstances_EscrowedInstanceId",
                table: "MailAttachments",
                column: "EscrowedInstanceId",
                principalTable: "ItemInstances",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MailAttachments_ItemInstances_EscrowedInstanceId",
                table: "MailAttachments");

            migrationBuilder.DropIndex(
                name: "IX_MailAttachments_EscrowedInstanceId",
                table: "MailAttachments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances");

            migrationBuilder.DropColumn(
                name: "EscrowedInstanceId",
                table: "MailAttachments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances",
                sql: "Container IN (101, 104, 105, 109, 111, 112, 113, 115)");
        }
    }
}
