using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Faolan.Importer.V2.Modules
{
	// Item identities from inventory/snapshot packets. Wraps CaptureItemImporter (upsert by item key).
	// TODO(importer-v2 step 2a): thread ctx.Mode into CaptureItemImporter so Insert skips existing keys.
	public sealed class ItemsModule : IImportModule
	{
		public string Name => "items";
		public string Description => "Item identities from inventory/ApplySpell packets.";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var packets = ctx.Captures.SelectMany(c => c.Packets.Select(p => p.Bytes)).ToList();
			var (total, inserted) = await CaptureItemImporter.ImportAsync(ctx.Db, packets);
			return new ImportResult($"{total} item-candidate packets, {inserted} new.");
		}
	}
}
