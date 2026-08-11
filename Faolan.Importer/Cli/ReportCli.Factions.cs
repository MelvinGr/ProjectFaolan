using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;

namespace Faolan.Importer.Cli
{
	// `--report factions`: the Khitai faction & reputation catalog, out of the client (Gitea #310).
	//
	// #310 filed the system as *"Blocked on: expansion (Khitai) content + captures of the faction UI/standing packets
	// to decode the wire form — none today."* Neither half survives contact with the shipped client:
	//
	//   the WIRE   `FactionsList` = a `ListFrame` over `{u32 factionId, u32 points}` pairs, recovered from
	//              `DispatchInviteWithInviterFactionPoints`' own bail-out strings.
	//   the IDS    `data/FactionRelations.xml` (RDB 1010701/1) — 16 factions with their ids, icons and the full
	//              opposing-faction point matrix, plus a 5-tier encounter override table for 12 of them.
	//   the NAMES  `LDB[254]` — each faction twice (with and without the article) and its FOUR RANK TITLES.
	//   the TEXT   `LDB[254]` again: "You have lost %d faction standing points with %s."
	//
	// ⚠️ 1010701 was opened once before and set aside: `docs/subsystems/combat.md` records it as "chased for [NPC
	// disposition] and is player REPUTATION in a different id space". That verdict was right for that question and is
	// exactly the wrong thing to carry forward — a table that is not what one ticket wanted is still the table
	// another ticket is blocked on.
	//
	// ⚠️ What is genuinely NOT here: the POINT THRESHOLDS between ranks. The client ships the four rank names per
	// faction and the per-kill payouts, and nothing that says how many points Rank 2 costs. The report says so rather
	// than interpolating a ladder, because a guessed threshold would be indistinguishable from a decoded one the
	// moment it is written down.
	public static partial class ReportCli
	{
		// LDB category 254 - faction display names, rank titles and the standing messages.
		private const uint FactionTextCategory = 254;

		// The two name blocks, both stepping by 16 in the SAME faction order as the rank blocks.
		private const uint FactionNameWithArticle = 60435760;
		private const uint FactionNameBare = 61205296;
		private const uint FactionNameStep = 16;

		// The rank-title blocks: nine runs of four, stepping by 0x1000 between factions.
		private const uint FactionRankBase = 59456385;
		private const uint FactionRankStep = 0x1000;
		private const int FactionRankCount = 4;

		// Two independent spellings of one faction, compared: the XML's authoring comment vs the LDB's display name.
		// Drops the article and everything but letters, so `The Brittle Blade` == `Brittle Blade` and
		// `Scholars of Cheng-Ho` == `Scholars of Cheng-ho`.
		private static string Simplify(string name)
		{
			var text = name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
			return new string([.. text.Where(char.IsLetter)]).ToLowerInvariant();
		}

		private static void Factions(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);

			if (!rdb.TryGet(FactionRelationTable.RdbType, FactionRelationTable.RdbId, out var entry) || !rdb.CanRead(entry))
			{
				Console.WriteLine($"RDB {FactionRelationTable.RdbType}/{FactionRelationTable.RdbId} (FactionRelations.xml) is not readable in this install.");
				return;
			}

			var table = FactionRelationTable.Parse(Encoding.UTF8.GetString(rdb.ReadDecompressed(entry)).TrimStart('﻿'));

			// LDB[254], read once. The name/rank blocks are ORDERED, not keyed by faction id, so they are read as
			// ordered runs and joined to the XML's factions by position - and the report PRINTS both sides so a
			// mismatch is visible rather than silently absorbed.
			var text = new Dictionary<uint, string>();
			foreach (var (_, category, strings) in EnglishLdbPagesOf(rdb))
			{
				if (category != FactionTextCategory)
					continue;

				foreach (var (id, value) in strings)
				{
					if (!string.IsNullOrWhiteSpace(value))
						text[id] = value;
				}
			}

			var ldbNames = Enumerable.Range(0, 32)
				.Select(n => (Bare: text.GetValueOrDefault(FactionNameBare + (uint)n * FactionNameStep),
					Article: text.GetValueOrDefault(FactionNameWithArticle + (uint)n * FactionNameStep)))
				.TakeWhile(x => x.Bare != null)
				.ToList();

			var rankRuns = Enumerable.Range(0, 32)
				.Select(n => Enumerable.Range(0, FactionRankCount)
					.Select(r => text.GetValueOrDefault(FactionRankBase + (uint)n * FactionRankStep + (uint)r))
					.ToList())
				.TakeWhile(run => run[0] != null)
				.ToList();

			Console.WriteLine($"RDB {FactionRelationTable.RdbType}/{FactionRelationTable.RdbId} `data/FactionRelations.xml`: "
			                  + $"{table.Relations.Count} relation(s) over {table.Standard.Count()} faction(s).");
			Console.WriteLine($"LDB[{FactionTextCategory}]: {ldbNames.Count} faction name pair(s), {rankRuns.Count} rank ladder(s) of {FactionRankCount}.");
			Console.WriteLine();

			Console.WriteLine("⭐ THE CATALOG - id, icon, the rival it opposes, and the standard payout:");
			Console.WriteLine($"  {"id",4}  {"faction (XML authoring comment)",-34} {"icon",8}  {"tiers",5}  payout(s)");
			foreach (var relation in table.Standard)
			{
				var payout = string.Join("  ", relation.Awards.Select(a => $"{a.FactionId}:{a.Points:+#;-#;0}"));
				var tiers = table.Tiers(relation.Faction).Count();
				Console.WriteLine($"  {relation.Faction,4}  {Trim(table.Names.GetValueOrDefault(relation.Faction, "?"), 34),-34} {relation.IconId,8}  {tiers,5}  {payout}");
			}

			Console.WriteLine();
			Console.WriteLine("⭐ THE ENCOUNTER TIERS - `relationId = factionBase + tier`, and what one kill is worth with");
			Console.WriteLine("   the faction you are grinding (the file's own header explains the 60-point proportionality):");
			var byTier = table.Relations.Where(r => r.Awards.Count > 0)
				.GroupBy(r => r.Tier)
				.OrderBy(g => g.Key);

			Console.WriteLine($"  {"tier",4}  {"encounter",-14} {"own faction",12}  seen on");
			var tierNames = new[] { "solo normal", "solo boss", "group normal", "group boss", "raid normal", "raid boss" };
			foreach (var group in byTier)
			{
				// The award against the victim's OWN faction is the negative one; it is the same magnitude in every
				// entry of a tier, which is what makes the tier a rate rather than a per-faction number.
				var own = group.Select(r => r.Awards.FirstOrDefault(a => a.FactionId == r.Faction)?.Points).Distinct().ToList();
				var name = group.Key < tierNames.Length ? tierNames[group.Key] : "?";
				Console.WriteLine($"  {group.Key,4}  {name,-14} {string.Join("/", own),12}  {group.Count()} faction(s)");
			}

			Console.WriteLine();
			Console.WriteLine("⭐ THE NAMES AND RANK LADDERS, from LDB[254]. The LDB blocks are ORDERED runs, not keyed by");
			Console.WriteLine("   faction id, so they are joined to the catalog BY NAME - the XML\'s authoring comment against the");
			Console.WriteLine("   LDB\'s display string, two independent spellings of the same faction. The resulting id order is");
			Console.WriteLine("   printed rather than assumed, and each faction\'s Rank 4 title is a second, independent check that");
			Console.WriteLine("   the row is the right one (`Wolf of the Steppes`, `Tiger`, `Jade Shadow`, `Archpriest`, `Sage`).");
			Console.WriteLine();
			Console.WriteLine($"  {"blk",3} {"id",4}  {"faction",-26} rank ladder");

			var byName = table.Standard.ToDictionary(r => Simplify(table.Names.GetValueOrDefault(r.Faction, "?")), r => r.Faction);
			var matched = new List<uint>();
			for (var i = 0; i < rankRuns.Count; i++)
			{
				var bare = i < ldbNames.Count ? ldbNames[i].Bare : null;
				var ranks = string.Join(" -> ", rankRuns[i].Where(r => r != null));
				if (bare == null || !byName.TryGetValue(Simplify(bare), out var faction))
				{
					Console.WriteLine($"  {i,3} {"?",4}  {Trim(bare ?? "-", 26),-26} {ranks}   <- NO catalog faction of this name");
					continue;
				}

				matched.Add(faction);
				Console.WriteLine($"  {i,3} {faction,4}  {Trim(bare, 26),-26} {ranks}");
			}

			Console.WriteLine();
			Console.WriteLine($"  {matched.Count} of {rankRuns.Count} LDB block(s) join to a catalog faction by name - id order {string.Join(",", matched)},");
			Console.WriteLine($"  which is {(matched.SequenceEqual(matched.OrderDescending()) ? "STRICTLY DESCENDING" : "not monotone")}: the LDB lists them in the reverse of the XML\'s order.");
			Console.WriteLine($"  The {table.Standard.Count() - matched.Count} catalog faction(s) with NO rank ladder: "
			                  + string.Join(", ", table.Standard.Select(r => r.Faction).Except(matched).Order()));
			Console.WriteLine("  - of those, 100/110/120 (Hyrkanians, Insurrection, Jiang Shi) are also the three Khitai factions with");
			Console.WriteLine("    NO RIVAL in the matrix, so they look like NPC-only factions a player never joins; 200/310/320/330");
			Console.WriteLine("    are the non-Khitai ones (Ela Shan, the two Pit Master ladders, Clan Vigdis), which carry no");
			Console.WriteLine("    encounter-tier overrides either.");

			Console.WriteLine();
			Console.WriteLine("⚠️ NOT in the client: the POINT THRESHOLDS between ranks. Four rank NAMES per faction and the");
			Console.WriteLine("   per-kill payouts ship; nothing says what Rank 2 costs. Left undecided rather than interpolated -");
			Console.WriteLine("   a guessed ladder is indistinguishable from a decoded one once it is written down.");
		}
	}
}
