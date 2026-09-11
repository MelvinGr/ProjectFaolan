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
	/// <summary>
	///     Per-character learned spell sets, re-granted on login (DispatchLearnSpell). Needs characters and spells.
	/// </summary>
	public sealed class LearnedSpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(SpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character learned spell sets (0x200A grants).";
		public string Name => "learned-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (chars, rows) = await CaptureSpellImporter.ImportLearnedSpellsAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{chars} characters seeded, {rows} new rows.");
		}
	}
}
