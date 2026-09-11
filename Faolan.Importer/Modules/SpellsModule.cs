using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Cast/applied spell ids (ApplySpell) -> Spells.
	/// </summary>
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

			// A cast whose effect lives on a different record (Frozen Hatred). Needs the packets in order per capture -
			// the pairing is "the self-buff that answered this cast" - so it takes the per-capture lists.
			var (pairs, linked) = await CaptureSpellImporter.ImportCastEffectsAsync(ctx.Db, ctx.Captures.Select(c => (IReadOnlyList<byte[]>)[.. c.Packets.Select(x => x.Bytes)]).ToList());
			return new ImportResult($"{seen} spells seen, {inserted} new, {linked} cast->effect links ({pairs} candidate pairs).");
		}
	}
}
