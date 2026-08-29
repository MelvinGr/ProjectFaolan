using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class QuestLineTrailerWords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 = the SOLO line category, which is what 61 of the 97 captured index blocks carry and the
            // overwhelming majority of lines are (#896). Scaffolded as 0; hand-edited to 1 so an existing row that
            // no captured block covers lands on the right value instead of on a category the client never sends.
            migrationBuilder.AddColumn<uint>(
                name: "TrailerCategory",
                table: "QuestLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1u);

            migrationBuilder.AddColumn<uint>(
                name: "TrailerFlag",
                table: "QuestLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrailerCategory",
                table: "QuestLines");

            migrationBuilder.DropColumn(
                name: "TrailerFlag",
                table: "QuestLines");
        }
    }
}
