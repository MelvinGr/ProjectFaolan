using Microsoft.EntityFrameworkCore.Migrations;

namespace Faolan.Migrations
{
    public partial class _3 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                "HeadMesh",
                "Characters");

            migrationBuilder.DropColumn(
                "Size",
                "Characters");

            migrationBuilder.DropColumn(
                "SkinColor",
                "Characters");

            migrationBuilder.DropColumn(
                "Voice",
                "Characters");

            migrationBuilder.AddColumn<uint>(
                "HeadMesh",
                "CharacterBodies",
                "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<byte>(
                "Size",
                "CharacterBodies",
                "INTEGER",
                nullable: false,
                defaultValue: (byte) 0);

            migrationBuilder.AddColumn<byte>(
                "SkinColor",
                "CharacterBodies",
                "INTEGER",
                nullable: false,
                defaultValue: (byte) 0);

            migrationBuilder.AddColumn<byte>(
                "Voice",
                "CharacterBodies",
                "INTEGER",
                nullable: false,
                defaultValue: (byte) 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                "HeadMesh",
                "CharacterBodies");

            migrationBuilder.DropColumn(
                "Size",
                "CharacterBodies");

            migrationBuilder.DropColumn(
                "SkinColor",
                "CharacterBodies");

            migrationBuilder.DropColumn(
                "Voice",
                "CharacterBodies");

            migrationBuilder.AddColumn<uint>(
                "HeadMesh",
                "Characters",
                "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<byte>(
                "Size",
                "Characters",
                "INTEGER",
                nullable: false,
                defaultValue: (byte) 0);

            migrationBuilder.AddColumn<byte>(
                "SkinColor",
                "Characters",
                "INTEGER",
                nullable: false,
                defaultValue: (byte) 0);

            migrationBuilder.AddColumn<byte>(
                "Voice",
                "Characters",
                "INTEGER",
                nullable: false,
                defaultValue: (byte) 0);
        }
    }
}