using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AccountGmLevel : Migration
    {
        /// <inheritdoc />
        // GmGodMode and GmInvisible were the SAME data as two bits of the client's own gm_level mask - GodMode
        // (0x200) and InvisibleToMonsters (0x80) - and the mask itself was derived from WebAccountType rather than
        // stored. All of it collapses into one nullable column: the mask, or NULL for an account that is not a GM.
        //
        // ⚠️ No backfill: the roles this used to derive CONTAIN both bits, so materialising one would switch god
        // mode and aggro-invisibility on for an existing GM - and a GM who is invulnerable without knowing it is
        // #902 exactly (damage lands, the character dies, the health bar never moves). The one account on this
        // deployment was carried over by hand instead.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GmGodMode",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "GmInvisible",
                table: "Accounts");

            migrationBuilder.AddColumn<uint>(
                name: "GmLevel",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GmLevel",
                table: "Accounts");

            migrationBuilder.AddColumn<bool>(
                name: "GmGodMode",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "GmInvisible",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
