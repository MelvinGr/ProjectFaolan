using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     DoT effects (StatDamageIIR) -> Spell.DotDelta/DotType and Spell.DotIntervalMs.
	/// </summary>
	public sealed class StatDamageModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(SpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "DoT per-tick deltas + measured tick intervals from StatDamageIIR packets.";
		public string Name => "dot-effects";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// Per-capture packet lists, not ctx.RawPackets: the interval is measured from the time between ticks, and
			// the tick trains it measures are keyed by session-volatile entity handles, so they must not span captures.
			var (spells, updated, intervals) = await CaptureStatDamageImporter.ImportAsync(ctx.Db,
				[.. ctx.Captures.Select(c => (IReadOnlyList<CapturedPacket>)c.Packets)]);
			return new ImportResult($"{spells} DoT spells seen, {updated} written ({intervals} tick intervals measured).");
		}
	}
}
