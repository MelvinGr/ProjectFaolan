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
	// Summonable mount creatures (#27) into the dedicated never-loaded Mounts map. Isolated; NOT in the default
	// set, so opt-in via --only mounts.
	public sealed class MountsModule : IImportModule
	{
		// NpcEquipment.Template is an FK to Items (the worn item's quad + hash), so items must be harvested first.
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Summonable mount creatures (#27, opt-in).";
		public string Name => "mounts";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CaptureMountImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} mount creature(s) found, {seeded} seeded.");
		}
	}
}
