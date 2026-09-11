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
	// NPC hostility disposition (cCategory) per NPC, harvested from the captured RequestAggroStatus responses - the
	// only correct hostility source. An NPC's Community is faction membership, not hostility (community 1 is the
	// civilian/default faction shared by Citizens/vendors/guards), so a `Community > 0` aggro test makes every
	// townsperson hostile. Writes NpcStatCore.Disposition (0 friendly / 1 hostile / 2 neutral); the sim gates aggro
	// (only 1 aggros) and the overhead name colour on it. Depends on `npcs` (the stat cores to attach to must
	// exist).
	//
	// Four tiers, strongest evidence first - own poll, seen attacking, faction roll-up, then the static mob rule that
	// fills what the corpus never saw. `NpcDispositionTiers` owns the order and `--report disposition-rule` scores the
	// last one; see both before changing the ranking.
	public sealed class NpcDispositionModule : IImportModule
	{
		private const long NullCommunity = NpcDispositionTiers.NullCommunity; // sentinel for NPCs with no NpcStats.community

		// No hard DependsOn: it attaches to whatever NpcStatCores exist, and registration order already sequences it
		// after `npcs` in a full default run. (A hard dep would force `npcs` -> `characters` ->... into a targeted
		// `--only npc-disposition` run; it just no-ops on an empty DB, which is fine.)
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "NPC hostility disposition (cCategory from RequestAggroStatus).";
		public string Name => "npc-disposition";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// Harvest (npc template -> cCategory votes) across all captures.
			//
			// Keyed by template, not by name. 38 templates ship as "Halen van Dummy" - the training-dummy
			// placeholder winning over the real name - so a name -> community step silently funnelled every vote for
			// any of them into whichever faction the last one written happened to belong to. The template is what the
			// stat core is keyed by anyway, so the name was never anything but a lossy detour.
			var templateVotes = new Dictionary<uint, Dictionary<uint, int>>();
			foreach (var cap in ctx.Captures)
			foreach (var (templateId, category) in NpcDispositionExtractor.Extract(cap.Packets))
			{
				if (!templateVotes.TryGetValue(templateId, out var byCat))
					templateVotes[templateId] = byCat = [];
				byCat[category] = byCat.GetValueOrDefault(category) + 1;
			}

			// A template that was polled gets its own answer; a template that was never polled falls back to its
			// faction's majority, and one whose faction was never polled either stays null.
			//
			// Per-template first, never faction-first. With the votes correctly attributed, almost every polled
			// template votes unanimously, and the few that do not are guards splitting friendly/neutral. The
			// per-template signal lines up exactly with the client's own colour table - traders/villagers/citizens
			// green, mobs red, guards yellow - a split a faction roll-up cannot express, because guards and the
			// citizens they protect share a community.
			//
			// A roll-up is also wrong where a faction holds two populations: community 346 polls 4 templates
			// unanimously neutral and 2 unanimously hostile, so a majority paints all six the same.
			//
			// The faction fallback stays for reach: it is what fills a template no capture ever targeted, from its
			// faction-mates that were. Null-community NPCs (unique story NPCs like Casilda/Kalanthes) share one
			// bucket.
			var cores = await ctx.Db.NpcStatCores.ToListAsync();
			var commByTemplate = cores.ToDictionary(c => c.NpcTemplateId, c => c.Community ?? NullCommunity);

			var commVotes = new Dictionary<long, Dictionary<uint, int>>();
			foreach (var (templateId, votes) in templateVotes)
			{
				var comm = commByTemplate.TryGetValue(templateId, out var c)
					? c
					: NullCommunity;
				if (!commVotes.TryGetValue(comm, out var byCat))
					commVotes[comm] = byCat = [];
				foreach (var (cat, n) in votes)
					byCat[cat] = byCat.GetValueOrDefault(cat) + n;
			}

			var commDisposition = commVotes.ToDictionary(kv => kv.Key, kv => Majority(kv.Value));
			var templateDisposition = templateVotes.ToDictionary(kv => kv.Key, kv => Majority(kv.Value));

			// The third tier: a creature the corpus saw attack is hostile, whether or not anyone polled it.
			// Without it a template no client ever asked about - and whose faction-mates were never asked about
			// either - keeps a null disposition, which the world model reads as friendly: 154 templates over 645
			// placements stood around green and passive, the beach crocodiles among them.
			var seenAttacking = new HashSet<uint>();
			foreach (var cap in ctx.Captures)
				seenAttacking.UnionWith(NpcDispositionExtractor.ExtractAttackers(cap.Packets));

			// The fourth and last tier: a creature the corpus never polled, never caught fighting, and whose
			// whole faction was never polled either, decided from the static columns - a real non-civilian community
			// plus the mob collision group. It is the only INFERENCE in the chain, which is why it sits below every
			// observation and why the module reports how it scores instead of just how often it fired: the shape it
			// has to avoid is the 2026-07-11 regression, a rule that was right for wildlife and wrong for townsfolk.
			// `--report disposition-rule` is the measurement, against the directly-polled templates.
			var updated = 0;
			var hostile = 0;
			var byTier = new Dictionary<NpcDispositionTiers.Tier, int>();
			foreach (var core in cores)
			{
				var (resolved, tier) = NpcDispositionTiers.Resolve(core, templateDisposition, seenAttacking, commDisposition);
				byTier[tier] = byTier.GetValueOrDefault(tier) + 1;
				if (resolved is not { } disposition)
					continue; // nothing said anything -> leave NULL, and say so below

				if (core.Disposition != disposition)
				{
					core.Disposition = disposition;
					updated++;
				}

				if (disposition == NpcDispositionTiers.Hostile)
					hostile++;
			}

			await ctx.Db.SaveChangesAsync();

			// A creature with no disposition is served as friendly and passive, so the count is worth stating rather
			// than leaving as the difference between two other numbers.
			var stillNull = cores.Count(c => c.Disposition == null);
			return new ImportResult(
				$"{templateDisposition.Count} templates polled directly, {commDisposition.Count} factions dispositioned; " +
				$"{updated} stat cores set ({byTier.GetValueOrDefault(NpcDispositionTiers.Tier.OwnVotes)} from their own votes, " +
				$"{byTier.GetValueOrDefault(NpcDispositionTiers.Tier.SeenAttacking)} from being seen attacking, " +
				$"{byTier.GetValueOrDefault(NpcDispositionTiers.Tier.FactionRollUp)} from their faction, " +
				$"{byTier.GetValueOrDefault(NpcDispositionTiers.Tier.MobRule)} from the mob rule, {hostile} hostile); " +
				$"{stillNull} still unknown (served friendly).");
		}

		// The winning category of a vote tally. Ties break to the lowest category, so a 50/50 lands on the more
		// permissive reading rather than on whichever key the dictionary happened to enumerate first.
		private static uint Majority(Dictionary<uint, int> byCategory)
		{
			return byCategory.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
		}
	}
}
