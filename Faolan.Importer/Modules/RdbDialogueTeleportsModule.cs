using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// the `{teleportplayer}` travellers, whose destination is inline in their dialogue text rather than in a paid
	// destination list. RDB-backed for the reason every module in this file is - the text is in the client's install
	// and the capture modules run without an RdbDir - and, more sharply, because the directive never reaches the
	// server at runtime: a conversation screen carries only an LDB reference, and the server ships no LDB.
	public sealed class RdbDialogueTeleportsModule : IImportModule
	{
		// The screens to read and the edges to key the rows by both come from the conversation graph; the maps have to
		// exist for a destination to be resolvable. Both come in through `travel-wiring`, which is named directly
		// rather than reached transitively: it OWNS this table's captured rows, and the conversation rebuild it sits
		// behind cascades them away (TravelDestination FKs ConversationNode). Depending on `conversations` alone left
		// a `--only rdb-dialogue-teleports` run with the four captured wagoner trips deleted and only these three
		// directive trips put back.
		public IReadOnlyList<string> DependsOn => [nameof(TravelWiringModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;
		public bool InDefaultSet => false;

		public string Description => "Local travel from inline {teleportplayer} dialogue directives (#635, opt-in).";
		public string Name => "rdb-dialogue-teleports";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbDialogueTeleportImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);

			// Every directive it did not turn into a trip is accounted for by reason - a silent skip is
			// indistinguishable from a decode regression.
			return new ImportResult(
				$"{r.Directives} {{teleportplayer}} directive(s) across {r.Screens} screen(s): {r.Added} trip(s) added, {r.Updated} updated, " +
				$"{r.CapturedWins} left to an existing CAPTURED trip on the same branch, {r.NoInboundEdge} on a screen no branch reaches, " +
				$"{r.UnknownMap} naming a playfield we have not imported; {r.Unreadable} screen(s) failed to decrypt.");
		}
	}
}
