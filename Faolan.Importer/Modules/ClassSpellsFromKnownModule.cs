using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	// Class<->spell membership derived from the captured spellbooks, which the client's own catalog cannot reach for a
	// spell record carrying no stacking line. Reads the DB rather than the packets: it runs over whatever
	// `CharacterSpells` holds, and both capture paths write that - the 0x200A grants (learned-spells) and the
	// snapshot's known-spell list (characters).
	//
	// It declares no dependency on either of them, and its position in `ModuleRegistry` (immediately after
	// learned-spells) is what orders it - the same shape as `NpcTypeAppearanceModule`. Naming them would be more
	// honest about the data flow and is wrong in practice: both resolve back to `characters`, which REFUSES to run
	// without `--force` on a populated DB, so `--only class-spells-known` would abort on a rebuild it does not need.
	// This is a derivation over rows that already exist, and it is correct against whatever is there.
	public sealed class ClassSpellsFromKnownModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Db;

		public string Description => "Class<->spell membership derived from the captured spellbooks (#851).";
		public string Name => "class-spells-known";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (pairs, rows) = await CaptureSpellImporter.ImportClassSpellsFromKnownAsync(ctx.Db);
			return new ImportResult($"{pairs} capture-proven (class, spell) pairs, {rows} new rows.");
		}
	}
}
