using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// the NPC combat ARCHETYPE (`NpcStatCore.ComboClass`) - which `combo_npc_<class>_*` family a template may
	// swing beside its generic row. Harvested, because the archetype is in no column we hold and in no client table:
	// the captured swing carries the combo id, and the client's own `Combos.NpcClass` states the class.
	//
	// A separate module rather than a step of `npcs`, for the same reason `npc-disposition` is: it only STAMPS a
	// column on stat cores that already exist. Registration order sequences it after `npcs`.
	public sealed class NpcComboClassModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "NPC combat archetype (which combo_npc_<class>_* family a template swings), from captured combo swings.";
		public string Name => "npc-combo-class";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// The catalog is the ORACLE: `NpcClass` is the client's own `npcclass` attribute, so a name is never
			// parsed. -1 is the generic set (no class) and null is a row that states nothing - neither votes.
			var classOf = await ctx.Db.Combos.Where(c => c.NpcClass != null && c.NpcClass >= 0)
				.ToDictionaryAsync(c => c.Id, c => c.NpcClass!.Value);

			var votes = new Dictionary<uint, Dictionary<int, int>>();
			foreach (var cap in ctx.Captures)
			foreach (var (templateId, comboId) in NpcComboClassExtractor.Extract(cap.Packets))
			{
				if (!classOf.TryGetValue(comboId, out var npcClass))
					continue; // a generic row, a player combo, or a combo the catalog does not hold

				if (!votes.TryGetValue(templateId, out var byClass))
					votes[templateId] = byClass = [];
				byClass[npcClass] = byClass.GetValueOrDefault(npcClass) + 1;
			}

			// A tally rather than a single value, so a CONFLICT is visible instead of being silently resolved by
			// whichever swing was walked last. The corpus has none - all 30 harvested templates vote unanimously -
			// and that is the measurement this module reports, not an assumption it makes.
			var conflicted = votes.Where(v => v.Value.Count > 1).ToList();

			var cores = await ctx.Db.NpcStatCores.ToListAsync();
			var updated = 0;
			foreach (var core in cores)
			{
				if (!votes.TryGetValue(core.NpcTemplateId, out var byClass))
					continue; // never caught swinging a class family -> stays NULL, generic set only

				var winner = byClass.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
				if (core.ComboClass == winner)
					continue;

				core.ComboClass = winner;
				updated++;
			}

			await ctx.Db.SaveChangesAsync();
			var families = votes.SelectMany(v => v.Value.Keys).Distinct().Count();
			return new ImportResult($"{votes.Count} template(s) caught swinging a class family ({families} distinct classes), {updated} stat core(s) stamped"
			                        + (conflicted.Count > 0 ? $"; ⚠ {conflicted.Count} template(s) voted for MORE THAN ONE class" : "; no template voted for two classes") + ".");
		}
	}
}
