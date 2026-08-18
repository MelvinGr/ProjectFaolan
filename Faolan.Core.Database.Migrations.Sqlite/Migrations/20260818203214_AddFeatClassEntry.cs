using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatClassEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatClassEntries",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false),
                    Class = table.Column<byte>(type: "INTEGER", nullable: true),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    FeatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Autotrain = table.Column<bool>(type: "INTEGER", nullable: false),
                    TreeId = table.Column<uint>(type: "INTEGER", nullable: false),
                    TreeColumn = table.Column<uint>(type: "INTEGER", nullable: false),
                    Flag = table.Column<uint>(type: "INTEGER", nullable: false),
                    Tier = table.Column<uint>(type: "INTEGER", nullable: false),
                    TreePointsRequired = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatClassEntries", x => x.Id);
                    table.CheckConstraint("CK_FeatClassEntry_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatClassEntries");
        }
    }
}
