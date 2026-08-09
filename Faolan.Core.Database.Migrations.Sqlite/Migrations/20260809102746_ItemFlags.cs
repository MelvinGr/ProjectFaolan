using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ItemFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "Flags",
                table: "Items",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Flags",
                table: "Items");
        }
    }
}
