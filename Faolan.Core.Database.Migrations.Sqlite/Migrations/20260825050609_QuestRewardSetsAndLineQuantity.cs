using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestRewardSetsAndLineQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Optional",
                table: "QuestRewardOptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // every pre-existing row was an offer OPTION (#893)

            migrationBuilder.AddColumn<uint>(
                name: "Quantity",
                table: "QuestRewardOptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1u);

            migrationBuilder.AddColumn<uint>(
                name: "Quantity",
                table: "QuestLineRewards",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Optional",
                table: "QuestRewardOptions");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "QuestRewardOptions");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "QuestLineRewards");
        }
    }
}
