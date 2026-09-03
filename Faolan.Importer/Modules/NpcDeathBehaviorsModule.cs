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
	// Per-creature death animation (the death BccIIR behaviorId), harvested from captured NPC kills (#362). Enriches
	// NpcTemplate.DeathBehaviorId by name; un-harvested types keep NULL and the runtime falls back to the humanoid id.
	public sealed class NpcDeathBehaviorsModule : IImportModule
	{
		// No hard DependsOn (mirrors NpcDispositionModule): enriches whatever NpcTemplates exist; registration order
		// sequences it after `npcs`. No-ops on an empty DB.
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-creature death animation (death BccIIR behaviorId from captured NPC kills).";
		public string Name => "npc-death-behaviors";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var harvested = NpcDeathBehaviorExtractor.Merge(ctx.Captures.SelectMany(c => NpcDeathBehaviorExtractor.Extract(c.Packets)));
			var byName = harvested.ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);

			var cleared = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite)
			{
				foreach (var t in await ctx.Db.NpcTemplates.Where(t => t.DeathBehaviorId != null || t.DeathActivationTime != null).ToListAsync())
				{
					t.DeathBehaviorId = null;
					t.DeathActivationTime = null;
					cleared++;
				}
			}

			// A name can map to several templates (e.g. two "Nemedian Archer" types) - every matching type gets the id.
			var updated = 0;
			var timed = 0;
			foreach (var template in await ctx.Db.NpcTemplates.ToListAsync())
			{
				if (template.Name == null || !byName.TryGetValue(template.Name, out var death))
					continue;

				if (template.DeathBehaviorId != death.BehaviorId)
				{
					template.DeathBehaviorId = death.BehaviorId;
					updated++;
				}

				// A zero means no record of this creature stated a length (see the extractor), and NULL is how that is
				// carried - writing 0 would send a zero-length death animation instead of falling back to the
				// animation's own (#781).
				var activation = death.ActivationTime == 0 ? (uint?)null : death.ActivationTime;
				if (template.DeathActivationTime != activation)
				{
					template.DeathActivationTime = activation;
					timed += activation == null ? 0 : 1;
				}
			}

			await ctx.Db.SaveChangesAsync();
			var distinct = byName.Values.Select(d => d.BehaviorId).Distinct().Count();
			var prefix = cleared > 0
				? $"cleared {cleared}; "
				: "";
			return new ImportResult($"{prefix}{byName.Count} creature(s) harvested ({distinct} distinct death anims), {updated} templates set, {timed} with a measured animation length.");
		}
	}
}
