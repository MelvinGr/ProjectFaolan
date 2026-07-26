using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ClassAttributeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemStatModifier_StatId",
                table: "ItemStatModifiers");

            migrationBuilder.CreateTable(
                name: "ClassAttributeBases",
                columns: table => new
                {
                    Class = table.Column<byte>(type: "INTEGER", nullable: false),
                    Level = table.Column<uint>(type: "INTEGER", nullable: false),
                    StatId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassAttributeBases", x => new { x.Class, x.Level, x.StatId });
                    table.CheckConstraint("CK_ClassAttributeBase_Class", "Class IN (18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44)");
                    table.CheckConstraint("CK_ClassAttributeBase_StatId", "StatId IN (0, 1, 11, 12, 20, 27, 33, 34, 51, 52, 54, 57, 58, 59, 60, 61, 70, 75, 79, 91, 106, 127, 140, 157, 165, 169, 173, 174, 186, 187, 191, 215, 224, 225, 226, 241, 243, 275, 278, 279, 280, 291, 310, 311, 312, 319, 331, 334, 350, 353, 355, 363, 389, 448, 450, 451, 454, 456, 458, 475, 488, 490, 491, 498, 504, 505, 506, 507, 577, 594, 595, 596, 597, 607, 656, 658, 666, 667, 668, 669, 670, 671, 711, 713, 804, 806, 808, 810, 814, 818, 824, 828, 830, 861, 871, 872, 875, 878, 901, 920, 922, 923, 925, 926, 927, 928, 929, 965, 1007, 1035, 1085, 1093, 1097, 1098, 1099, 1332, 1376, 1380, 1382, 1383, 1385, 1386, 1387, 1390, 1392, 1393, 1395, 1397)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemStatModifier_StatId",
                table: "ItemStatModifiers",
                sql: "StatId IN (0, 1, 11, 12, 20, 27, 33, 34, 51, 52, 54, 57, 58, 59, 60, 61, 70, 75, 79, 91, 106, 127, 140, 157, 165, 169, 173, 174, 186, 187, 191, 215, 224, 225, 226, 241, 243, 275, 278, 279, 280, 291, 310, 311, 312, 319, 331, 334, 350, 353, 355, 363, 389, 448, 450, 451, 454, 456, 458, 475, 488, 490, 491, 498, 504, 505, 506, 507, 577, 594, 595, 596, 597, 607, 656, 658, 666, 667, 668, 669, 670, 671, 711, 713, 804, 806, 808, 810, 814, 818, 824, 828, 830, 861, 871, 872, 875, 878, 901, 920, 922, 923, 925, 926, 927, 928, 929, 965, 1007, 1035, 1085, 1093, 1097, 1098, 1099, 1332, 1376, 1380, 1382, 1383, 1385, 1386, 1387, 1390, 1392, 1393, 1395, 1397)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassAttributeBases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemStatModifier_StatId",
                table: "ItemStatModifiers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemStatModifier_StatId",
                table: "ItemStatModifiers",
                sql: "StatId IN (0, 1, 11, 12, 20, 27, 33, 34, 51, 52, 54, 57, 58, 59, 60, 61, 70, 75, 79, 91, 106, 127, 140, 157, 165, 169, 173, 174, 186, 187, 191, 215, 224, 225, 226, 241, 243, 275, 291, 310, 311, 312, 319, 331, 334, 350, 353, 355, 363, 389, 448, 450, 451, 454, 456, 458, 475, 488, 490, 491, 498, 504, 505, 506, 507, 577, 594, 595, 596, 597, 607, 656, 658, 666, 667, 668, 669, 670, 671, 711, 713, 804, 806, 808, 810, 814, 818, 824, 828, 830, 861, 871, 872, 875, 878, 901, 920, 922, 923, 925, 926, 927, 928, 929, 965, 1007, 1035, 1085, 1093, 1097, 1098, 1099, 1332, 1376, 1380, 1382, 1383, 1385, 1386, 1387, 1390, 1392, 1393, 1395, 1397)");
        }
    }
}
