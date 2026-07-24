using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Faolan.Core.Database.Migrations.Postgres.Migrations
{
	/// <inheritdoc />
	public partial class ConversationTreeNaturalKeys : Migration
	{
		// The dialogue tree moves off identity surrogates onto position-derived keys (see ConversationNode): the node
		// id is derived from its identity, and each child is keyed by its parent's key + its Sequence. DROP+CREATE
		// rather than the scaffolded ALTER: EF's scaffold RENAMES each table's Id column into a new key column, which
		// would carry surrogate values forward as node ids / sequence numbers - garbage keys. The whole tree is
		// capture-derived content with nothing outside pointing into it, so it is rebuilt by re-running the importer's
		// conversations module (or by loading faolan.sql, which is regenerated from it).
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(name: "ConversationScreenParameters");
			migrationBuilder.DropTable(name: "ConversationScreens");
			migrationBuilder.DropTable(name: "ConversationPackets");
			migrationBuilder.DropTable(name: "ConversationNodes");

			migrationBuilder.CreateTable(
				name: "ConversationNodes",
				columns: table => new
				{
					Id = table.Column<long>(type: "bigint", nullable: false),
					ConditionQuestId = table.Column<long>(type: "bigint", nullable: true),
					GrantQuestId = table.Column<long>(type: "bigint", nullable: true),
					NpcTemplateId = table.Column<long>(type: "bigint", nullable: true),
					TypeId = table.Column<long>(type: "bigint", nullable: false),
					ConditionQuestState = table.Column<int>(type: "integer", nullable: true),
					AnswerPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
					NpcName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationNodes", x => x.Id);
					table.ForeignKey(
						name: "FK_ConversationNodes_NpcTemplates_NpcTemplateId",
						column: x => x.NpcTemplateId,
						principalTable: "NpcTemplates",
						principalColumn: "TemplateId",
						onDelete: ReferentialAction.Restrict);
					table.ForeignKey(
						name: "FK_ConversationNodes_QuestDefinitions_GrantQuestId",
						column: x => x.GrantQuestId,
						principalTable: "QuestDefinitions",
						principalColumn: "QuestId");
				});

			migrationBuilder.CreateTable(
				name: "ConversationPackets",
				columns: table => new
				{
					ConversationNodeId = table.Column<long>(type: "bigint", nullable: false),
					Sequence = table.Column<int>(type: "integer", nullable: false),
					SubOpcode = table.Column<long>(type: "bigint", nullable: true),
					Env = table.Column<byte>(type: "smallint", nullable: true),
					Flag = table.Column<byte>(type: "smallint", nullable: true),
					ShotSize = table.Column<byte>(type: "smallint", nullable: true),
					ShotType = table.Column<byte>(type: "smallint", nullable: true),
					ShotEmphasis = table.Column<byte>(type: "smallint", nullable: true),
					ShotFov = table.Column<byte>(type: "smallint", nullable: true),
					ShotTilt = table.Column<byte>(type: "smallint", nullable: true),
					Autoface = table.Column<bool>(type: "boolean", nullable: true),
					HeadPaddingY = table.Column<float>(type: "real", nullable: true),
					Field = table.Column<long>(type: "bigint", nullable: true),
					SenderInstance = table.Column<long>(type: "bigint", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationPackets", x => new
					{
						x.ConversationNodeId,
						x.Sequence
					});
					table.ForeignKey(
						name: "FK_ConversationPackets_ConversationNodes_ConversationNodeId",
						column: x => x.ConversationNodeId,
						principalTable: "ConversationNodes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "ConversationScreens",
				columns: table => new
				{
					ConversationNodeId = table.Column<long>(type: "bigint", nullable: false),
					PacketSequence = table.Column<int>(type: "integer", nullable: false),
					Sequence = table.Column<int>(type: "integer", nullable: false),
					ScreenId = table.Column<long>(type: "bigint", nullable: false),
					Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
					Category = table.Column<long>(type: "bigint", nullable: false),
					Knubot = table.Column<long>(type: "bigint", nullable: false),
					Antitag = table.Column<long>(type: "bigint", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationScreens", x => new
					{
						x.ConversationNodeId,
						x.PacketSequence,
						x.Sequence
					});
					table.ForeignKey(
						name: "FK_ConversationScreens_ConversationPackets_ConversationNodeId_~",
						columns: x => new
						{
							x.ConversationNodeId,
							x.PacketSequence
						},
						principalTable: "ConversationPackets",
						principalColumns: new[] { "ConversationNodeId", "Sequence" },
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "ConversationScreenParameters",
				columns: table => new
				{
					ConversationNodeId = table.Column<long>(type: "bigint", nullable: false),
					PacketSequence = table.Column<int>(type: "integer", nullable: false),
					ScreenSequence = table.Column<int>(type: "integer", nullable: false),
					Sequence = table.Column<int>(type: "integer", nullable: false),
					InnerRaw = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
					Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationScreenParameters", x => new
					{
						x.ConversationNodeId,
						x.PacketSequence,
						x.ScreenSequence,
						x.Sequence
					});
					table.ForeignKey(
						name: "FK_ConversationScreenParameters_ConversationScreens_Conversati~",
						columns: x => new
						{
							x.ConversationNodeId,
							x.PacketSequence,
							x.ScreenSequence
						},
						principalTable: "ConversationScreens",
						principalColumns: new[] { "ConversationNodeId", "PacketSequence", "Sequence" },
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_ConversationNodes_GrantQuestId",
				table: "ConversationNodes",
				column: "GrantQuestId");

			migrationBuilder.CreateIndex(
				name: "IX_ConversationNodes_NpcTemplateId",
				table: "ConversationNodes",
				column: "NpcTemplateId");

			migrationBuilder.CreateIndex(
				name: "IX_ConversationNodes_NpcName_TypeId_AnswerPath",
				table: "ConversationNodes",
				columns: new[] { "NpcName", "TypeId", "AnswerPath" });
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(name: "ConversationScreenParameters");
			migrationBuilder.DropTable(name: "ConversationScreens");
			migrationBuilder.DropTable(name: "ConversationPackets");
			migrationBuilder.DropTable(name: "ConversationNodes");

			migrationBuilder.CreateTable(
				name: "ConversationNodes",
				columns: table => new
				{
					Id = table.Column<long>(type: "bigint", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
					ConditionQuestId = table.Column<long>(type: "bigint", nullable: true),
					GrantQuestId = table.Column<long>(type: "bigint", nullable: true),
					NpcTemplateId = table.Column<long>(type: "bigint", nullable: true),
					TypeId = table.Column<long>(type: "bigint", nullable: false),
					ConditionQuestState = table.Column<int>(type: "integer", nullable: true),
					AnswerPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
					NpcName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
					CapturedInstance = table.Column<long>(type: "bigint", nullable: false),
					CapturedNpcHandle = table.Column<long>(type: "bigint", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationNodes", x => x.Id);
					table.ForeignKey(
						name: "FK_ConversationNodes_NpcTemplates_NpcTemplateId",
						column: x => x.NpcTemplateId,
						principalTable: "NpcTemplates",
						principalColumn: "TemplateId",
						onDelete: ReferentialAction.Restrict);
					table.ForeignKey(
						name: "FK_ConversationNodes_QuestDefinitions_GrantQuestId",
						column: x => x.GrantQuestId,
						principalTable: "QuestDefinitions",
						principalColumn: "QuestId");
				});

			migrationBuilder.CreateTable(
				name: "ConversationPackets",
				columns: table => new
				{
					Id = table.Column<long>(type: "bigint", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
					ConversationNodeId = table.Column<long>(type: "bigint", nullable: false),
					SubOpcode = table.Column<long>(type: "bigint", nullable: true),
					Env = table.Column<byte>(type: "smallint", nullable: true),
					Flag = table.Column<byte>(type: "smallint", nullable: true),
					Sequence = table.Column<int>(type: "integer", nullable: false),
					ShotSize = table.Column<byte>(type: "smallint", nullable: true),
					ShotType = table.Column<byte>(type: "smallint", nullable: true),
					ShotEmphasis = table.Column<byte>(type: "smallint", nullable: true),
					ShotFov = table.Column<byte>(type: "smallint", nullable: true),
					ShotTilt = table.Column<byte>(type: "smallint", nullable: true),
					Autoface = table.Column<bool>(type: "boolean", nullable: true),
					HeadPaddingY = table.Column<float>(type: "real", nullable: true),
					Field = table.Column<long>(type: "bigint", nullable: true),
					SenderInstance = table.Column<long>(type: "bigint", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationPackets", x => x.Id);
					table.ForeignKey(
						name: "FK_ConversationPackets_ConversationNodes_ConversationNodeId",
						column: x => x.ConversationNodeId,
						principalTable: "ConversationNodes",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "ConversationScreens",
				columns: table => new
				{
					Id = table.Column<long>(type: "bigint", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
					ConversationPacketId = table.Column<long>(type: "bigint", nullable: false),
					ScreenId = table.Column<long>(type: "bigint", nullable: false),
					Sequence = table.Column<int>(type: "integer", nullable: false),
					Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
					Category = table.Column<long>(type: "bigint", nullable: false),
					Knubot = table.Column<long>(type: "bigint", nullable: false),
					Antitag = table.Column<long>(type: "bigint", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationScreens", x => x.Id);
					table.ForeignKey(
						name: "FK_ConversationScreens_ConversationPackets_ConversationPacketId",
						column: x => x.ConversationPacketId,
						principalTable: "ConversationPackets",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "ConversationScreenParameters",
				columns: table => new
				{
					Id = table.Column<long>(type: "bigint", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
					ConversationScreenId = table.Column<long>(type: "bigint", nullable: false),
					Sequence = table.Column<int>(type: "integer", nullable: false),
					InnerRaw = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
					Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ConversationScreenParameters", x => x.Id);
					table.ForeignKey(
						name: "FK_ConversationScreenParameters_ConversationScreens_Conversati~",
						column: x => x.ConversationScreenId,
						principalTable: "ConversationScreens",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_ConversationNodes_GrantQuestId",
				table: "ConversationNodes",
				column: "GrantQuestId");

			migrationBuilder.CreateIndex(
				name: "IX_ConversationNodes_NpcTemplateId",
				table: "ConversationNodes",
				column: "NpcTemplateId");

			migrationBuilder.CreateIndex(
				name: "IX_ConversationNodes_NpcName_TypeId_AnswerPath_ConditionQuestI~",
				table: "ConversationNodes",
				columns: new[] { "NpcName", "TypeId", "AnswerPath", "ConditionQuestId", "ConditionQuestState" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_ConversationPackets_ConversationNodeId",
				table: "ConversationPackets",
				column: "ConversationNodeId");

			migrationBuilder.CreateIndex(
				name: "IX_ConversationScreens_ConversationPacketId",
				table: "ConversationScreens",
				column: "ConversationPacketId");

			migrationBuilder.CreateIndex(
				name: "IX_ConversationScreenParameters_ConversationScreenId",
				table: "ConversationScreenParameters",
				column: "ConversationScreenId");
		}
	}
}
