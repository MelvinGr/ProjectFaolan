using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// The Khitai faction catalog and its kill-payout matrix, straight out of the client (Gitea #310).
	//
	// TWO sources, and neither is a capture - which is the whole point, since #310 was filed as blocked on captures
	// that do not exist and the corpus carries zero faction packets in either direction:
	//
	//   the RULESET   `data/FactionRelations.xml` (RDB 1010701/1) - 16 factions with their ids, icons, rivals and
	//                 the per-encounter-tier payout matrix. Plaintext XML; FactionRelationTable owns the grammar.
	//   the NAMES     LDB[254] - the display name and the four rank titles per faction.
	//
	// ⚠️ The LDB blocks are ORDERED RUNS, not keyed by faction id, so they cannot be indexed by id. They are read as
	// runs and joined to the XML by NAME, and the join is REPORTED rather than assumed: the XML's authoring comment
	// and the LDB's display name are two independent spellings of one faction, so a name that fails to join is a
	// finding rather than something to absorb. (The LDB lists them in the reverse of the XML's order - 90, 80, … 10 -
	// which is exactly the kind of thing a positional join would get silently wrong.)
	//
	// Each faction's rank-4 title is a second, independent check that a row is the right one: `Wolf of the Steppes`,
	// `Tiger`, `Jade Shadow`, `Archpriest` and `Sage` each name their own faction, so an off-by-one shows up at once.
	//
	// See docs/subsystems/factions.md.
	public static class RdbFactionImporter
	{
		private const uint LdbType = 1030002;

		// LDB category 254 - faction display names, rank titles and the standing messages.
		private const uint FactionTextCategory = 254;

		// The bare-name block, stepping by 16 in the same faction order as the rank blocks.
		private const uint FactionNameBare = 61205296;
		private const uint FactionNameStep = 16;

		// The rank-title blocks: runs of four, stepping by 0x1000 between factions. A faction WITH a run is one a
		// player can hold standing with; the seven without are NPC-only reputations.
		private const uint FactionRankBase = 59456385;
		private const uint FactionRankStep = 0x1000;
		private const int FactionRankCount = 4;

		// How many ordered runs to walk before giving up. Sixteen factions ship and nine carry ladders; 32 is slack.
		private const int MaxRuns = 32;

		public sealed record Result(int Factions, int Awards, int Named, int Unjoined);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string proxyBase, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			if (!rdb.TryGet(FactionRelationTable.RdbType, FactionRelationTable.RdbId, out var entry) || !rdb.CanRead(entry))
				throw new InvalidOperationException(
					$"RDB {FactionRelationTable.RdbType}/{FactionRelationTable.RdbId} (FactionRelations.xml) is not readable in this install - "
					+ "the faction ruleset is shipped client data, so this is a broken/partial RDB rather than a gap.");

			// TrimStart the BOM: the file ships as UTF-8 with one, and XDocument.Parse rejects it.
			var table = FactionRelationTable.Parse(Encoding.UTF8.GetString(rdb.ReadDecompressed(entry)).TrimStart('﻿'));

			var (names, ladders) = ReadLdb(rdb);

			// The join, by simplified NAME in both directions. `byName` maps the LDB's spelling onto its run index so
			// a faction can find its own ladder; the XML's authoring comment is the other spelling.
			var ldbIndexByName = names
				.Select((name, index) => (name, index))
				.Where(x => !string.IsNullOrWhiteSpace(x.name))
				.ToDictionary(x => Simplify(x.name!), x => x.index);

			var rows = new List<Faction>();
			var awards = new List<FactionKillAward>();
			int named = 0, unjoined = 0;

			foreach (var standard in table.Standard)
			{
				var comment = table.Names.GetValueOrDefault(standard.Faction, "");
				var joined = ldbIndexByName.TryGetValue(Simplify(comment), out var index);
				if (joined)
					named++;
				else
					unjoined++;

				// The RIVAL is derived rather than declared: the standard entry's award list costs the victim's own
				// faction and grants the same amount to exactly one other, which is the rival. Read as "the award
				// that is POSITIVE", so a faction with no rival simply has none.
				var rival = standard.Awards.FirstOrDefault(a => a.Points > 0 && a.FactionId != standard.Faction);

				rows.Add(new Faction
				{
					Id = standard.Faction,
					Name = joined ? names[index]! : comment,
					IconId = standard.IconId,
					RivalFactionId = rival?.FactionId,
					// A player rank ladder is what says a player can join at all - see Faction.HasPlayerRanks.
					HasPlayerRanks = joined && ladders.Count > index && ladders[index].All(t => !string.IsNullOrWhiteSpace(t))
				});

				foreach (var tier in table.Tiers(standard.Faction))
				foreach (var award in tier.Awards)
				{
					awards.Add(new FactionKillAward
					{
						VictimFactionId = standard.Faction,
						Tier = tier.Tier,
						FactionId = award.FactionId,
						Points = award.Points
					});
				}

				if (!joined)
					logger?.LogWarning("RdbFactionImporter: faction {Id} '{Comment}' has no LDB[254] display name - kept under its XML comment.",
						standard.Faction, comment);
			}

			// Delete-then-insert, scoped to the whole catalog: the client's file is authoritative for it, so a faction
			// that disappears from the file is gone rather than stale. The awards go first - they point at the rows.
			//
			// ⚠️ EXPLICITLY, not by relying on the awards' FK cascade. A cascade does not fire when the principal is
			// removed and re-added with the same key in one unit of work: EF collapses that pair into an UPDATE, so no
			// DELETE reaches the database - which is exactly the bug that made the spell importer non-idempotent
			// (#807). CharacterFactionStandings is NOT cleared, for the obvious reason: it is play state.
			db.FactionKillAwards.RemoveRange(await db.FactionKillAwards.ToListAsync());
			db.Factions.RemoveRange(await db.Factions.ToListAsync());
			await db.SaveChangesAsync();

			db.Factions.AddRange(rows);
			await db.SaveChangesAsync();
			db.FactionKillAwards.AddRange(awards);
			await db.SaveChangesAsync();

			logger?.LogInformation(
				"RdbFactionImporter: {Factions} faction(s) ({Named} named from LDB[{Category}], {Unjoined} on their XML comment), "
				+ "{Awards} kill-award row(s) over {Tiers} tier(s); {Ranked} faction(s) carry a player rank ladder.",
				rows.Count, named, FactionTextCategory, unjoined, awards.Count,
				awards.Select(a => a.Tier).Distinct().Count(), rows.Count(r => r.HasPlayerRanks));

			return new Result(rows.Count, awards.Count, named, unjoined);
		}

		// The LDB[254] ordered runs: the bare display names, and the four-title rank ladders beside them.
		private static (List<string?> Names, List<List<string?>> Ladders) ReadLdb(RdbReader rdb)
		{
			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);
			var text = new Dictionary<uint, string>();

			foreach (var e in rdb.EntriesOfType(LdbType).Where(rdb.CanRead))
			{
				if (!languageByPage.TryGetValue(e.Id, out var language) || language != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // not an IOz1/zlib page
				}

				if (LdbStringIndex.CategoryOf(body) != FactionTextCategory)
					continue;

				foreach (var s in LdbStringIndex.Parse(body))
				{
					if (!string.IsNullOrWhiteSpace(s.Value))
						text[s.Id] = s.Value;
				}
			}

			var names = Enumerable.Range(0, MaxRuns)
				.Select(n => text.GetValueOrDefault(FactionNameBare + (uint)n * FactionNameStep))
				.TakeWhile(name => name != null)
				.ToList();

			var ladders = Enumerable.Range(0, MaxRuns)
				.Select(n => Enumerable.Range(0, FactionRankCount)
					.Select(r => text.GetValueOrDefault(FactionRankBase + (uint)n * FactionRankStep + (uint)r))
					.ToList())
				.ToList();

			return (names, ladders);
		}

		// Two independent spellings of one faction, compared: the XML's authoring comment vs the LDB's display name.
		// Drops the article and everything but letters, so `The Brittle Blade` == `Brittle Blade` and
		// `Scholars of Cheng-Ho` == `Scholars of Cheng-ho`.
		private static string Simplify(string name)
		{
			var text = name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

			return new string([.. text.Where(char.IsLetter)]).ToLowerInvariant();
		}
	}
}
