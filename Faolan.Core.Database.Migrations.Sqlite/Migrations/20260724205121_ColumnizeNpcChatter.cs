using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
	/// <inheritdoc />
	public partial class ColumnizeNpcChatter : Migration
	{
		// The ambient bark stops being a verbatim `<remoteformat …>` blob and becomes typed columns, like its
		// StockPhrase sibling (#410) - see NpcChatter / RemoteFormatXml.BuildChatter. That also gives the table a
		// real key: the token addresses a LINE within a named CHANNEL, so (type, channel, sayinchat) identifies the
		// row and the AUTOINCREMENT surrogate goes.
		//
		// DROP+CREATE rather than the scaffolded ALTER: EF's scaffold renames the outgoing `Id` column into one of
		// the new data columns, which would carry surrogate values in as chatter values. The table is a dozen rows of
		// purely capture-derived content, rebuilt by `--only npc-chatter` - a module with NO dependencies, so
		// re-running it touches nothing else.
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(name: "NpcChatter");

			migrationBuilder.CreateTable(
				name: "NpcChatter",
				columns: table => new
				{
					NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
					Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
					SayInChat = table.Column<uint>(type: "INTEGER", nullable: false),
					ChannelCount = table.Column<uint>(type: "INTEGER", nullable: false),
					Group = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
					Category = table.Column<uint>(type: "INTEGER", nullable: false),
					Knubot = table.Column<uint>(type: "INTEGER", nullable: false),
					FormatKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
					ParameterName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_NpcChatter", x => new
					{
						x.NpcTemplateId,
						x.Channel,
						x.SayInChat
					});
					table.ForeignKey(
						name: "FK_NpcChatter_NpcTemplates_NpcTemplateId",
						column: x => x.NpcTemplateId,
						principalTable: "NpcTemplates",
						principalColumn: "TemplateId",
						onDelete: ReferentialAction.Cascade);
				});
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(name: "NpcChatter");

			migrationBuilder.CreateTable(
				name: "NpcChatter",
				columns: table => new
				{
					Id = table.Column<uint>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
					NpcTemplateId = table.Column<uint>(type: "INTEGER", nullable: false),
					RemoteFormat = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_NpcChatter", x => x.Id);
					table.ForeignKey(
						name: "FK_NpcChatter_NpcTemplates_NpcTemplateId",
						column: x => x.NpcTemplateId,
						principalTable: "NpcTemplates",
						principalColumn: "TemplateId",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_NpcChatter_NpcTemplateId",
				table: "NpcChatter",
				column: "NpcTemplateId");
		}
	}
}
