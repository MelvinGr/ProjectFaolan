using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class TravelDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TravelDestinations",
                columns: table => new
                {
                    NpcName = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationMapId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Cost = table.Column<uint>(type: "INTEGER", nullable: false),
                    ArrivalX = table.Column<float>(type: "REAL", nullable: true),
                    ArrivalY = table.Column<float>(type: "REAL", nullable: true),
                    ArrivalZ = table.Column<float>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelDestinations", x => new { x.NpcName, x.DestinationMapId });
                    table.ForeignKey(
                        name: "FK_TravelDestinations_Maps_DestinationMapId",
                        column: x => x.DestinationMapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TravelDestinations_DestinationMapId",
                table: "TravelDestinations",
                column: "DestinationMapId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TravelDestinations");
        }
    }
}
