using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MapPlayfieldFieldAC : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "PlayfieldFieldA",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            // 7, NOT 0: 7 is what BuildPlayfieldFiir hardcoded before #451, so every existing row keeps the exact
            // behaviour it had. A 0 default would also be indistinguishable from the genuine 0 that maps 1010, 2010,
            // 2040 and 5007 carry.
            migrationBuilder.AddColumn<uint>(
                name: "PlayfieldFieldC",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlayfieldFieldA",
                table: "Maps");

            migrationBuilder.DropColumn(
                name: "PlayfieldFieldC",
                table: "Maps");
        }
    }
}
