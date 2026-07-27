using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddClassComboScaling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassComboScalings",
                columns: table => new
                {
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    DpsModifier = table.Column<uint>(type: "INTEGER", nullable: false),
                    SecondaryModifier = table.Column<uint>(type: "INTEGER", nullable: false),
                    StaminaDivisor = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassComboScalings", x => new { x.Class, x.Level });
                    table.CheckConstraint("CK_ClassComboScaling_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassComboScalings");
        }
    }
}
