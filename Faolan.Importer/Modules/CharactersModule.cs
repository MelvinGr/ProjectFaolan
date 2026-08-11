using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	// Captured characters (incl. other players) -> Characters + child tables. Depends on items (worn
	// equipment references item rows).
	//
	// INSERT adds characters the DB does not have and leaves every existing row alone - the safe default, and what
	// a plain `dotnet run ... --skip characters`-less run does.
	//
	// OVERWRITE is the #753 refresh: a character whose row carries `Character.ImportedFromCapture` is DELETED and
	// rebuilt from the current corpus, so a re-dumped capture can update an imported bag / worn set / stat sheet
	// that would otherwise stay frozen at whichever dump first built it. A row without the marker was created
	// in-game and is never touched, which is what makes this safe where a blanket re-import was not: that is how
	// `Adada` (level 4, locally created) was lost, and the level threshold below would not have saved it either.
	//
	// ⚠️ It DOES discard a player's own rearrangement of an imported character's bag. That is the trade the mode
	// asks for, which is why it is opt-in and not the default.
	public sealed class CharactersModule : IImportModule
	{
		// Fresh/tutorial captures (level <= 5) are skipped: they carry no trained feats and only a base ability
		// scaffold, so they add nothing to the reference set. The kept chars are the leveled retail captures.
		private const uint MinLevel = 5;
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Captured characters (incl. other players) and their bodies/gear.";
		public string Name => "characters";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var refresh = ctx.ModeFor(Name) == WriteMode.Overwrite;
			// ⚠️ GUARDED, and this is what `--force` was parsed for (nothing consumed it until now). The marker
			// makes the refresh safe for characters the importer did not build, but for the ones it did it still
			// throws away whatever the player has since done with that character's bag - so it takes a second,
			// explicit yes rather than riding in on a global `--mode overwrite` meant for some other module.
			if (refresh && !ctx.Force)
				throw new InvalidOperationException("characters: overwrite REBUILDS every capture-imported character and discards any rearrangement of their items. Re-run with --force if that is what you want.");

			var (seen, added, skipped, refreshed) = await CaptureCharacterImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets), MinLevel, refresh);
			return new ImportResult($"{seen} captured, {added} added, {refreshed} refreshed, {skipped} kept (already in DB{(refresh ? " and not capture-imported" : "")} or below level {MinLevel}).");
		}
	}
}
