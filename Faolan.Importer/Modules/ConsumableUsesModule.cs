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
	// Consumable ON-USE spells (Items.OnUseSpell): pairs each used item's TemplateActionIIR with the self-ApplySpell it
	// emits, so a use-a-consumable capture teaches the DB what buff a potion/food/drink applies. Depends on items (the
	// rows it sets the column on). Upsert - it only writes items already present.
	public sealed class ConsumableUsesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule), nameof(SpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Consumable on-use spells (TemplateActionIIR + ApplySpell) -> Items.OnUseSpell.";
		public string Name => "consumable-uses";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (harvested, set) = await CaptureItemUseImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => (IReadOnlyList<byte[]>)c.Packets.Select(p => p.Bytes).ToList()));
			return new ImportResult($"{harvested} on-use spells harvested, {set} set on Items.");
		}
	}
}
