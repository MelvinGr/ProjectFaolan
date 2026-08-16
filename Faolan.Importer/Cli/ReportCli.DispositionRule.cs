using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report disposition-rule`: is the LAST-RESORT hostility rule (`NpcDispositionTiers.IsMob`) better than the
	// default it replaced, and does it leave the townsfolk alone? (Gitea #841.)
	//
	// The chain that decides an NPC's overhead disposition is three OBSERVATIONS and one inference (see
	// `NpcDispositionTiers`). This report exists for the inference. It scores it - and its ablations, including the
	// rule the 2026-07-11 regression removed and the default that replaced it - against the templates retail was
	// captured answering for DIRECTLY, which is ground truth no tier of the chain contributed to.
	//
	// ⚠️ The success criterion is not "the crocodiles bite". A rule that is right for wildlife and wrong for townsfolk
	// is exactly what caused the regression: `Hostile = Community > 0` aggroed every Citizen, vendor and Gate Guard
	// because community 1 is the civilian/default faction. So the number that decides adoption is not accuracy but the
	// FRIENDLY-SIDE error - how many templates retail called friendly the rule would call hostile - and the check
	// FAILS on any of those rather than on a percentage.
	//
	// ⭐ **Why it is a REPORT and not a test**, the same argument as `--report npc-pose-check`: it scores importer
	// OUTPUT (the rule, and the `NpcStatCores.Disposition` it writes) against importer INPUT (the captured polls). The
	// two populations diverge the moment a capture is added - a new dump both labels templates the rule was scored on
	// and shrinks the unknown set it fires over - so every count here MOVES with the corpus and is printed rather than
	// pinned. What stays asserted is the one direction that can only be a defect.
	public static partial class ReportCli
	{
		private static async Task<int> DispositionRule(IReadOnlyList<NamedCapture> captures)
		{
			// Tier 1, and the ground truth for everything below: the templates retail answered a RequestAggroStatus
			// for. 406 of 414 vote unanimously (#731), so the majority is not papering over a split.
			var templateVotes = new Dictionary<uint, Dictionary<uint, int>>();
			foreach (var capture in captures)
			foreach (var (templateId, category) in NpcDispositionExtractor.Extract(capture.Packets))
			{
				var byCat = templateVotes.TryGetValue(templateId, out var t) ? t : templateVotes[templateId] = [];
				byCat[category] = byCat.GetValueOrDefault(category) + 1;
			}

			var polled = templateVotes.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key);

			var seenAttacking = new HashSet<uint>();
			foreach (var capture in captures)
				seenAttacking.UnionWith(NpcDispositionExtractor.ExtractAttackers(capture.Packets));

			await using var db = OpenDb();
			var cores = await db.NpcStatCores.AsNoTracking().ToListAsync();
			var names = await db.NpcTemplates.AsNoTracking().ToDictionaryAsync(t => t.TemplateId, t => t.Name);

			// The population the rule is scored over: polled templates we also hold a stat core for, so the rule's
			// columns are readable. A vote for a template the DB does not carry cannot be scored either way.
			var truth = cores.Where(c => polled.ContainsKey(c.NpcTemplateId)).ToList();

			Console.WriteLine($"disposition-rule over {captures.Count} capture(s): {polled.Count} template(s) polled directly, " +
				$"{truth.Count} of them with a stat core to score against ({seenAttacking.Count} template(s) caught attacking).");
			Console.WriteLine($"  ground truth: {truth.Count(c => polled[c.NpcTemplateId] == 0)} friendly, " +
				$"{truth.Count(c => polled[c.NpcTemplateId] == 1)} hostile, {truth.Count(c => polled[c.NpcTemplateId] == 2)} neutral.");

			// The ablations. Each is a "call this one hostile" predicate; everything it does not fire on keeps the
			// current behaviour, which is the friendly default - so the scoring below is deliberately BINARY. The two
			// middle rows are the rule the regression removed, in both of its readings.
			var candidates = new (string Name, Func<NpcStatCore, bool> Predicate)[]
			{
				("always friendly (today's default)", _ => false),
				("Community > 0 (the pre-2026-07-11 rule)", c => c.Community > 0),
				("Community not in {null, civilian 1}", c => c.Community > NpcDispositionTiers.CivilianCommunity),
				($"CarsGroup == {NpcDispositionTiers.MobCarsGroup} alone", c => c.CarsGroup == NpcDispositionTiers.MobCarsGroup),
				("⭐ SHIPPED: mob CarsGroup AND a real non-civilian community", NpcDispositionTiers.IsMob)
			};

			// A rule that scores well but fires on nothing has not fixed anything, and a rule scored only on the polled
			// population is scored on the population it will never see - so the same table carries both: how each
			// candidate does against ground truth, and how many of the templates that actually REACH the last resort
			// it would answer for.
			var commByTemplate = cores.ToDictionary(c => c.NpcTemplateId, c => (long?)c.Community ?? NpcDispositionTiers.NullCommunity);
			var commVotes = new Dictionary<long, Dictionary<uint, int>>();
			foreach (var (templateId, votes) in templateVotes)
			{
				var comm = commByTemplate.GetValueOrDefault(templateId, NpcDispositionTiers.NullCommunity);
				var byCat = commVotes.TryGetValue(comm, out var t) ? t : commVotes[comm] = [];
				foreach (var (cat, n) in votes)
					byCat[cat] = byCat.GetValueOrDefault(cat) + n;
			}

			var factionRollUp = commVotes.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key);

			var reachTier4 = cores.Where(c => NpcDispositionTiers.Resolve(c, polled, seenAttacking, factionRollUp).From
				is NpcDispositionTiers.Tier.MobRule or NpcDispositionTiers.Tier.Unknown).ToList();
			var filled = reachTier4.Where(NpcDispositionTiers.IsMob).ToList();

			Console.WriteLine();
			Console.WriteLine($"  {"rule",-56} {"hostile ok",10} {"missed",7} {"NEUTRAL→h",10} {"FRIENDLY→h",11} {"acc",7} {"fills",6}");
			foreach (var (name, predicate) in candidates)
			{
				int hit = 0, missed = 0, fromNeutral = 0, fromFriendly = 0;
				foreach (var core in truth)
				{
					var actual = polled[core.NpcTemplateId];
					var says = predicate(core);
					if (actual == 1)
					{
						if (says)
							hit++;
						else
							missed++;
					}
					else if (says)
					{
						if (actual == 2)
							fromNeutral++;
						else
							fromFriendly++;
					}
				}

				var correct = truth.Count - missed - fromNeutral - fromFriendly;
				Console.WriteLine($"  {name,-56} {hit,10} {missed,7} {fromNeutral,10} {fromFriendly,11} " +
					$"{correct / (double)truth.Count,7:P1} {reachTier4.Count(predicate),6}");
			}

			// Why the AND, since the collision group alone scores HIGHER above: the two differ only over the creatures
			// carrying the mob group in the civilian faction or in none, which is where the passive wildlife sits - the
			// rabbits, hens, deer, badgers and rats retail answers NEUTRAL for. The `fills` column is what settles it:
			// on the population that actually reaches the last resort the two answer the SAME templates, so the AND
			// costs nothing today, and over ground truth it halves the neutral-side error. A rule that makes a rabbit
			// bite is a visible defect; leaving a creature friendly is the status quo this tier improves on.
			Console.WriteLine();
			Console.WriteLine($"  ground truth for CarsGroup == {NpcDispositionTiers.MobCarsGroup}, split by the community half of the rule:");
			foreach (var group in new (string Label, Func<NpcStatCore, bool> In)[]
			{
				("real non-civilian community", c => c.Community > NpcDispositionTiers.CivilianCommunity),
				($"civilian community {NpcDispositionTiers.CivilianCommunity}", c => c.Community == NpcDispositionTiers.CivilianCommunity),
				("no community at all", c => c.Community == null)
			})
			{
				var slice = truth.Where(c => c.CarsGroup == NpcDispositionTiers.MobCarsGroup && group.In(c)).ToList();
				Console.WriteLine($"    {group.Label,-30} {slice.Count(c => polled[c.NpcTemplateId] == 0),3} friendly  " +
					$"{slice.Count(c => polled[c.NpcTemplateId] == 1),3} hostile  {slice.Count(c => polled[c.NpcTemplateId] == 2),3} neutral");
			}

			Console.WriteLine();
			Console.WriteLine($"  {reachTier4.Count} template(s) reach the last resort (no poll of their own, never caught attacking, faction never polled).");
			Console.WriteLine($"    {filled.Count} answered HOSTILE by the rule; {reachTier4.Count - filled.Count} still unknown and served friendly.");
			Console.WriteLine($"    placements behind them: {await Placements(db, filled)} now hostile, {await Placements(db, reachTier4.Except(filled).ToList())} still passive.");

			foreach (var core in filled.Take(12))
				Console.WriteLine($"      hostile: template {core.NpcTemplateId} {names.GetValueOrDefault(core.NpcTemplateId)} (community {core.Community})");
			if (filled.Count > 12)
				Console.WriteLine($"      ... and {filled.Count - 12} more");

			// The one assertion. A template retail was captured calling FRIENDLY that the rule would call hostile is
			// the regression signature - a townsperson that aggros - and no percentage excuses it.
			var regressions = truth.Where(c => polled[c.NpcTemplateId] == 0 && NpcDispositionTiers.IsMob(c)).ToList();
			Console.WriteLine();
			if (regressions.Count == 0)
			{
				Console.WriteLine("  ✅ the rule calls no directly-polled FRIENDLY template hostile.");
				return 0;
			}

			Console.WriteLine($"  ⚠️ {regressions.Count} template(s) retail answered FRIENDLY for would be made hostile by the rule:");
			foreach (var core in regressions)
				Console.WriteLine($"    template {core.NpcTemplateId} {names.GetValueOrDefault(core.NpcTemplateId)} (community {core.Community}, cars group {core.CarsGroup})");

			return 1;
		}

		private static async Task<int> Placements(Faolan.Core.Database.DatabaseContext db, IReadOnlyList<NpcStatCore> cores)
		{
			var ids = cores.Select(c => c.NpcTemplateId).ToHashSet();
			return await db.NpcSpawns.AsNoTracking().CountAsync(s => ids.Contains(s.NpcTemplateId));
		}
	}
}
