using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ItemTemplatePair : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "PairKey1",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "PairKey2",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "PairKey3",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "PairKey4",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TemplateId2",
                table: "Items",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PairKey1",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PairKey2",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PairKey3",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PairKey4",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "TemplateId2",
                table: "Items");
        }
    }
}
