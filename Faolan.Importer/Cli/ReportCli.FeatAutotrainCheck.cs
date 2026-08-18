using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report feat-autotrain-check`: does the client's own auto-train flag agree with what retail actually puts in
	// a character's feat list, and does one point per rank reproduce the point budget those lists spend? (#862)
	//
	// Two populations, and neither is ours:
	//   * `FeatClassEntry` - the client's FEATCLASS table (RDB 1000043), which marks 3,839 of its 9,916 rows
	//     `Autotrain`, i.e. trained by the client itself from the character's class and level.
	//   * `CharacterFeats` - the feat lists harvested off the captured player spawns' pre-IIR frame. Retail's answer.
	//
	// The claim under test is that they are complementary: every id retail sends is one the client does NOT
	// auto-train, so nothing our server grants needs to be in that list, and putting one there is a wire divergence
	// (`FeatHolder::TrainFeat` refuses one outright - "will break feats!").
	//
	// It also prints the COST arithmetic, which is the other half of #862: summing `rank + 1` over each captured
	// list reproduces `FeatRules.PointBudget` for that character's level exactly.
	//
	// ⭐ **Why a report and not a test** (Gitea #729/#849): the captured half lives in `CharacterFeats`, and
	// `faolan.sql` excludes every `Character*` table as per-developer play state - so the suite's fixture holds none
	// and the assertion would pass on an empty left side. The properties of the CODE and of the client table are
	// gated in `FeatSpellGrantTests` / `FeatRulesTests`; this is the one comparison that needs the real database.
	//
	// Returns non-zero when a captured character holds a feat the client auto-trains - the only direction that can
	// only be a defect. A budget that does not land exactly is PRINTED, not asserted: a character may simply not have
	// spent everything, and it is the captures that would have to move for that to become news.
	public static partial class ReportCli
	{
		private static async Task<int> FeatAutotrainCheck()
		{
			await using var db = OpenDb();

			var autotrained = await db.FeatClassEntries.AsNoTracking().Where(e => e.Autotrain).Select(e => e.FeatId).Distinct().ToListAsync();
			var trainable = await db.FeatClassEntries.AsNoTracking().Where(e => !e.Autotrain).Select(e => e.FeatId).Distinct().ToListAsync();
			var names = await db.FeatIdMaps.AsNoTracking()
				.Join(db.Feats.AsNoTracking(), m => m.TemplateId, f => f.Id, (m, f) => new { m.WireFeatId, f.Name })
				.ToDictionaryAsync(x => x.WireFeatId, x => x.Name);

			if (autotrained.Count == 0)
			{
				Console.WriteLine("feat-autotrain-check: FeatClassEntries is empty - run `--only rdb-feats` first.");
				return 1;
			}

			var auto = autotrained.ToHashSet();
			Console.WriteLine($"client featclass table: {auto.Count} auto-trained feats, {trainable.Count} trainable.");

			var characters = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name, c.Level }).ToListAsync();
			var offenders = 0;
			var checkedChars = 0;

			Console.WriteLine();
			Console.WriteLine($"{"character",-14} {"lvl",3} {"feats",5} {"points",6} {"budget",6}  verdict");
			foreach (var character in characters.OrderBy(c => c.Name))
			{
				var held = await db.CharacterFeats.AsNoTracking().Where(f => f.CharacterId == character.Id).Select(f => f.FeatId).ToListAsync();
				if (held.Count == 0)
					continue;

				checkedChars++;
				var spent = FeatRules.TotalCost(held);
				var budget = FeatRules.PointBudget(character.Level);
				var handed = held.Where(auto.Contains).ToList();
				offenders += handed.Count;

				var verdict = handed.Count > 0 ? $"⛔ {handed.Count} AUTO-TRAINED feat(s) in the list" : spent == budget ? "spends its budget exactly" : $"{budget - spent:+#;-#;0} unspent";
				Console.WriteLine($"{character.Name,-14} {character.Level,3} {held.Count,5} {spent,6} {budget,6}  {verdict}");

				foreach (var id in handed)
					Console.WriteLine($"    {id} {names.GetValueOrDefault(id, "?")} - retail auto-trains this; it must not be in a feat list");
			}

			if (checkedChars == 0)
			{
				Console.WriteLine("(no character in this database carries a feat - nothing to check)");
				return 0;
			}

			Console.WriteLine();
			Console.WriteLine(offenders == 0
				? $"OK: {checkedChars} character(s), no auto-trained feat in any feat list."
				: $"FAIL: {offenders} auto-trained feat(s) across {checkedChars} character(s).");

			return offenders == 0 ? 0 : 1;
		}
	}
}
