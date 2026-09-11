using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// Per-creature ambient ANIMATIONS - the idle behaviours a standing NPC performs, harvested from the
	// captured `BehaviorId.Emote` records. Keyed by template (like npc-chatter), so an un-captured creature simply
	// has none and stands still rather than being handed a guessed animation.
	public sealed class NpcAmbientAnimationsModule : IImportModule
	{
		// No hard DependsOn (mirrors npc-death-behaviors): it attaches to whatever NpcTemplates exist, and
		// registration order sequences it after `npcs`. No-ops on an empty DB.
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-creature ambient idle animations (captured BehaviorId.Emote records), keyed by template.";
		public string Name => "npc-ambient-animations";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var harvested = NpcAmbientAnimationExtractor.Merge(ctx.Captures.SelectMany(c => NpcAmbientAnimationExtractor.Extract(c.Packets)));
			var known = await ctx.Db.NpcTemplates.Select(t => t.TemplateId).ToHashSetAsync();

			// Delete+reinsert the harvested types' rows, which is why the key is natural (CLAUDE.md's conventions): a
			// re-harvest must not churn ids. Only the types this run harvested are cleared, so a narrower corpus does
			// not silently drop a creature whose animations were captured previously.
			var touched = harvested.Select(e => e.TemplateId).Where(known.Contains).ToHashSet();
			var stale = await ctx.Db.NpcAmbientAnimations.Where(a => touched.Contains(a.NpcTemplateId)).ToListAsync();
			ctx.Db.NpcAmbientAnimations.RemoveRange(stale);

			var rows = 0;
			var skipped = 0;
			foreach (var e in harvested)
			{
				if (!known.Contains(e.TemplateId))
				{
					skipped++;
					continue;
				}

				ctx.Db.NpcAmbientAnimations.Add(new Core.Database.Tables.NpcAmbientAnimation
				{
					NpcTemplateId = e.TemplateId,
					AnimationId = e.AnimationId,
					DurationCentiseconds = e.DurationCentiseconds,
					Weight = e.Weight
				});
				rows++;
			}

			await ctx.Db.SaveChangesAsync();
			var animations = harvested.Select(e => e.AnimationId).Distinct().Count();
			return new ImportResult($"{touched.Count} creature type(s), {rows} ambient animation row(s) ({animations} distinct animations)"
			                        + (skipped > 0 ? $"; {skipped} for templates not in the DB" : "") + ".");
		}
	}
}
