using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ItemBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Binding",
                table: "ItemInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemInstance_Binding",
                table: "ItemInstances",
                sql: "Binding IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemInstance_Binding",
                table: "ItemInstances");

            migrationBuilder.DropColumn(
                name: "Binding",
                table: "ItemInstances");
        }
    }
}
