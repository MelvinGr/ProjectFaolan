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
	// NPC-follows-NPC formation bindings, harvested from the captured mode-07 MoveTasks. Both ends are
	// resolved to creature names through the same capture's spawns, so the pair lands on NpcTemplate at both ends.
	public sealed class NpcFollowPairsModule : IImportModule
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

		public string Description => "NPC-follows-NPC formation bindings (captured mode-07 MoveTasks), keyed by template pair.";
		public string Name => "npc-follow-pairs";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// not Merge()'d to one leader per follower: a follower type legitimately has several leader types
			// (`Slave of Acheron` was captured behind both `Black Ring Hellsworn` and `Black Ring Defender`), and the
			// table is keyed to hold all of them.
			var harvested = ctx.Captures.SelectMany(c => NpcFollowPairExtractor.Extract(c.Packets))
				.Select(p => (p.Follower, p.Leader))
				.Distinct()
				.ToList();

			// name -> every template carrying it. A name can map to several templates (two "Nemedian Archer" types),
			// and the binding is a property of the creature, so every matching type gets it.
			var byName = (await ctx.Db.NpcTemplates.Where(t => t.Name != null).Select(t => new { t.TemplateId, t.Name }).ToListAsync())
				.GroupBy(t => t.Name!, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Select(t => t.TemplateId).ToList(), StringComparer.OrdinalIgnoreCase);

			var wanted = new HashSet<(uint Follower, uint Leader)>();
			var unresolved = 0;
			foreach (var (follower, leader) in harvested)
			{
				if (!byName.TryGetValue(follower, out var followers) || !byName.TryGetValue(leader, out var leaders))
				{
					unresolved++;
					continue;
				}

				foreach (var f in followers)
				foreach (var l in leaders)
				{
					if (f != l) // a creature does not follow itself; two templates sharing a name would say so
						wanted.Add((f, l));
				}
			}

			// Delete+reinsert the harvested FOLLOWERS' rows - natural key, so a re-harvest must not churn ids. Only
			// the followers this run saw are cleared, so a narrower corpus does not drop a pair captured earlier.
			var touched = wanted.Select(w => w.Follower).ToHashSet();
			ctx.Db.NpcFollowPairs.RemoveRange(await ctx.Db.NpcFollowPairs.Where(f => touched.Contains(f.FollowerTemplateId)).ToListAsync());

			foreach (var (follower, leader) in wanted)
			{
				ctx.Db.NpcFollowPairs.Add(new Core.Database.Tables.NpcFollowPair
				{
					FollowerTemplateId = follower,
					LeaderTemplateId = leader
				});
			}

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{harvested.Count} captured pair(s) -> {wanted.Count} template binding(s) over {touched.Count} follower type(s)"
			                        + (unresolved > 0 ? $"; {unresolved} naming a creature not in the DB" : "") + ".");
		}
	}
}
