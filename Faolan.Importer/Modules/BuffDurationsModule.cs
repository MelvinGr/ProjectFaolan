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
	// The buff duration retail states, for the spell rows whose RDB record leaves property field 8 empty.
	//
	// It is registered last in ModuleRegistry.All, after the rdb-* modules, and that placement is the whole
	// ordering contract: `rdb-spells` writes `Spells.DurationMs` unconditionally, so this has to run after it or its
	// three rows are silently reinstated at 0/1 ms. `RdbSpellsModule.RequiredAfter` names it for the same reason.
	public sealed class BuffDurationsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(SpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Buff durations retail states for spell rows the RDB leaves empty.";
		public string Name => "buff-durations";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// Per-capture lists, not ctx.RawPackets: the rule counts distinct captures stating a duration, which is
			// what separates a base duration from one restore's remaining time.
			var (seen, adopted) = await CaptureSpellImporter.ImportBuffDurationsAsync(ctx.Db,
				[.. ctx.Captures.Select(c => (IReadOnlyList<CapturedPacket>)c.Packets)]);
			return new ImportResult($"{seen} empty-duration spell rows with captured applies, {adopted} durations adopted from the wire.");
		}
	}
}
