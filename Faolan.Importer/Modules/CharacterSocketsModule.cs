using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	// The GEMS socketed into an existing character's items, from the captured login snapshots' slot_map.
	// A separate pass from `characters` because that module is insert-only - it skips a character already in the DB, so
	// a database that predates sockets could never pick them up, and re-importing the characters wholesale would delete
	// locally created ones. Needs characters (the items to attach to) + items (the gem templates).
	public sealed class CharacterSocketsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Gems socketed into characters' items (snapshot slot_map).";
		public string Name => "character-sockets";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await CaptureSocketImporter.ImportAsync(ctx.Db, ctx.Captures.SelectMany(c => c.Packets));
			var gap = r.UnresolvedGems.Count > 0
				? $" ⚠️  {r.UnresolvedGems.Count} gem template(s) missing from Items, sockets dropped: {string.Join(", ", r.UnresolvedGems.OrderBy(id => id))}."
				: string.Empty;
			// SocketedSeen vs Matched: an unmatched socketed item is a captured cell whose template is not the one the
			// DB holds there, which is a stale-corpus signal rather than a socket that failed to import - see
			// SocketImport.
			return new ImportResult($"{r.Characters} characters, {r.Sockets} sockets from {r.Matched}/{r.SocketedSeen} socketed items whose cell still matches.{gap}");
		}
	}

	// Vendor shop windows handled in ShopsModule.cs; characters in CharactersModule.cs.
}
