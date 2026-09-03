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
	// Class<->spell MEMBERSHIP derived from the captured spellbooks, which the client's own catalog cannot reach for a
	// spell record carrying no stacking line (#851). Reads the DB rather than the packets: it runs over whatever
	// `CharacterSpells` holds, and BOTH capture paths write that - the 0x200A grants (learned-spells) and the
	// snapshot's known-spell list (characters).
	//
	// ⚠️ It declares NO dependency on either of them, and its position in `ModuleRegistry` (immediately after
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
