using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NpcChatterParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParameterName",
                table: "NpcChatter");

            migrationBuilder.CreateTable(
                name: "NpcChatterParameter",
                columns: table => new
                {
                    NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SayInChat = table.Column<uint>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NpcChatterParameter", x => new { x.NpcTemplateId, x.Channel, x.SayInChat, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_NpcChatterParameter_NpcChatter_NpcTemplateId_Channel_SayInChat",
                        columns: x => new { x.NpcTemplateId, x.Channel, x.SayInChat },
                        principalTable: "NpcChatter",
                        principalColumns: new[] { "NpcTemplateId", "Channel", "SayInChat" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NpcChatterParameter");

            migrationBuilder.AddColumn<string>(
                name: "ParameterName",
                table: "NpcChatter",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }
    }
}
