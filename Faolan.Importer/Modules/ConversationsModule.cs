using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// NPC dialogue trees, condition-tagged per capture then merged. Always a clean rebuild (handle-keyed stale
	// rows must be purged), so Overwrite-only.
	public sealed class ConversationsModule : IImportModule
	{
		// Conversation nodes FK NpcTemplateId (NpcsModule, #244) and GrantQuestId (QuestsModule) - both must import
		// first so the references resolve (FK is enforced on every provider; no import-time deferral).
		public IReadOnlyList<string> DependsOn => [nameof(NpcsModule), nameof(QuestsModule)];

		// OVERWRITE only. The graph fold (#642) is a global partition over every harvested walk - which screens merge,
		// and which need a Discriminator, is decided by comparing them against each other - so it cannot be applied to
		// one new dump against a database that no longer remembers the walks the rest came from. The merge rule the
		// #419 upsert existed for ("the most complete capture of a screen wins") now runs INSIDE the fold, across all
		// captures at once, which is strictly the better place for it.
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "NPC dialogue graph: content-addressed screens + explicit option edges (whole-corpus rebuild).";
		public string Name => "conversations";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// The variant split runs over the WHOLE corpus rather than per capture: a screen is only a conflict
			// relative to the other captures of it, and an NPC's states usually come from different dumps (#526).
			var extracted = ctx.Captures
				.SelectMany(c => ConversationExtractor.Extract(c.Packets))
				.ToList();
			var variants = new List<string>();
			var split = ConversationVariants.SplitConflicts(extracted, variants);
			foreach (var line in variants)
				ctx.Out.WriteLine($"[conversations] {line}");

			var conversations = ConversationExtractor.Merge(split);
			var result = await ConversationImportOps.ImportConversations(ctx.Db, conversations, ctx.Force);
			foreach (var line in result.Report)
				ctx.Out.WriteLine($"[conversations] {line}");

			var held = result.Protected > 0 ? $", {result.Protected} left as hand-verified" : "";

			return new ImportResult($"{conversations.Count} harvested walks folded to {result.Nodes} screens, {result.Edges} option edges, {result.Entries} roots{held}.");
		}
	}
}
