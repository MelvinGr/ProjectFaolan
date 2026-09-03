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
	// Cast/applied spell ids (ApplySpell) -> Spells.
	public sealed class SpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Spell ids from ApplySpell packets.";
		public string Name => "spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, inserted) = await CaptureSpellImporter.ImportAsync(ctx.Db, ctx.RawPackets);

			// #434: a cast whose effect lives on a different record (Frozen Hatred). Needs the packets IN ORDER per
			// capture - the pairing is "the self-buff that answered this cast" - so it takes the per-capture lists.
			var (pairs, linked) = await CaptureSpellImporter.ImportCastEffectsAsync(ctx.Db, ctx.Captures.Select(c => (IReadOnlyList<byte[]>)[.. c.Packets.Select(x => x.Bytes)]).ToList());
			return new ImportResult($"{seen} spells seen, {inserted} new, {linked} cast->effect links ({pairs} candidate pairs).");
		}
	}
}
