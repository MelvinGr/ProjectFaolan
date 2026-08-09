using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class GuildBankVault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances");

            migrationBuilder.AddColumn<uint>(
                name: "OwnerGuildId",
                table: "ItemInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemInstances_OwnerGuildId_Container_Slot",
                table: "ItemInstances",
                columns: new[] { "OwnerGuildId", "Container", "Slot" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances",
                sql: "Container IN (101, 104, 105, 109, 111, 112, 113, 115)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemInstance_OneOwner",
                table: "ItemInstances",
                sql: "(OwnerCharacterId IS NULL) <> (OwnerGuildId IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ItemInstances_Guilds_OwnerGuildId",
                table: "ItemInstances",
                column: "OwnerGuildId",
                principalTable: "Guilds",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ItemInstances_Guilds_OwnerGuildId",
                table: "ItemInstances");

            migrationBuilder.DropIndex(
                name: "IX_ItemInstances_OwnerGuildId_Container_Slot",
                table: "ItemInstances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemInstance_OneOwner",
                table: "ItemInstances");

            migrationBuilder.DropColumn(
                name: "OwnerGuildId",
                table: "ItemInstances");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemInstance_Container",
                table: "ItemInstances",
                sql: "Container IN (101, 104, 105, 111, 112, 113, 115)");
        }
    }
}
