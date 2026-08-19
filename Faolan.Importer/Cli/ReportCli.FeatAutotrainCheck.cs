using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
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

			await AutoTrainedEffects(db);

			return offenders == 0 ? 0 : 1;
		}

		/// <summary>
		///     The other half of the same population (Gitea #863): what the auto-trained feats DO, now that #862 has
		///     settled that they are never in a feat list.
		/// </summary>
		/// <remarks>
		///     ⚠️ The ticket asked for one check before shipping this - that folding the effects in must not move a
		///     captured character's stat sheet, because the class PROGRESSION tracks carry 70-80 effect rows each. The
		///     check passes STRUCTURALLY rather than numerically, which is stronger: the derived set touches no pool
		///     and no attribute at all, so there is nothing for it to move. Both halves print below.
		///     <list type="number">
		///         <item>
		///             the progression track is EXCLUDED, because <c>ClassAttributeBases</c> already applies it - the
		///             same client templates, read through the RDB item parser instead of the feat catalog;
		///         </item>
		///         <item>
		///             what remains touches none of HealthMax / Strength / Constitution / Intelligence / Wisdom /
		///             Dexterity, which is asserted here and is the only direction that can be a defect.
		///         </item>
		///     </list>
		/// </remarks>
		private static async Task AutoTrainedEffects(DatabaseContext db)
		{
			// The stats a captured sheet would notice - the five attributes and the health pool. Nothing derived here
			// may carry one.
			CharacterStatId[] sheetStats =
			[
				CharacterStatId.HealthMax, CharacterStatId.Strength, CharacterStatId.Constitution,
				CharacterStatId.Intelligence, CharacterStatId.Wisdom, CharacterStatId.Dexterity
			];

			var rows = await (from entry in db.FeatClassEntries.AsNoTracking().Where(e => e.Autotrain && e.Class != null)
				join map in db.FeatIdMaps.AsNoTracking() on entry.FeatId equals map.WireFeatId
				join feat in db.Feats.AsNoTracking() on map.TemplateId equals feat.Id
				join effect in db.FeatEffects.AsNoTracking() on map.TemplateId equals effect.FeatId
				select new
				{
					Class = entry.Class!.Value,
					entry.Level,
					entry.Id,
					Name = feat.Name,
					Effect = effect
				}).ToListAsync();

			if (rows.Count == 0)
				return;

			var track = rows.Where(r => r.Name != null && r.Name.Contains(FeatRules.ClassProgressionTrackMarker, StringComparison.Ordinal)).ToList();
			var derived = rows.Except(track).ToList();

			Console.WriteLine();
			Console.WriteLine($"auto-trained EFFECTS (#863): {rows.Count} rows, of which {track.Count} are the class PROGRESSION track "
			                  + $"(already applied via ClassAttributeBases) and {derived.Count} are the derived source.");

			var leaked = derived.Where(r => sheetStats.Contains(r.Effect.StatId)).ToList();
			Console.WriteLine(leaked.Count == 0
				? "  ✅ the derived set touches NO pool or attribute stat, so no captured stat sheet can move."
				: $"  ⛔ {leaked.Count} derived row(s) carry a sheet stat - the captured sheets must be re-checked before this ships.");

			// The LADDER, per class at the level cap - the pick the shipping rule makes, beside the sum it refuses.
			// A sum is printed because it is the rival implementation, and the gap is the whole argument.
			Console.WriteLine();
			Console.WriteLine($"  {"class",-16} {"stats",5} {"picked",8} {"summed",8}  (the sum is what stacking every rung would give)");
			foreach (var group in derived.GroupBy(r => r.Class).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
			{
				var reached = group.Select(r => new AutoTrainedFeatLadder.Rung(r.Level, r.Id, r.Effect));
				var picked = AutoTrainedFeatLadder.Select(reached);
				Console.WriteLine($"  {group.Key.ToString(),-16} {picked.Select(e => e.StatId).Distinct().Count(),5} {picked.Sum(e => (long)e.Value),8} {group.Sum(r => (long)r.Effect.Value),8}");
			}

			await ManaRegenBaseCorroboration(db, derived.Select(r => (r.Class, r.Level, r.Id, r.Effect)));
		}

		/// <summary>
		///     ⭐ The corroboration the derivation was NOT fitted to: the out-of-combat mana-regen base.
		/// </summary>
		/// <remarks>
		///     <c>RegenRates</c> recorded three retail observations of stat 280 with no source for them - 0 for a
		///     level-23 Assassin, 2000 for a level-24 Herald of Xotli, 2200 for both level-80 mages. The auto-trained
		///     <c>Mage</c> / <c>Priest</c> feats plus the class base reproduce all three exactly, which is what says
		///     the ladder rule is reading the client rather than agreeing with itself.
		/// </remarks>
		private static async Task ManaRegenBaseCorroboration(DatabaseContext db,
			IEnumerable<(ConanClass Class, uint Level, uint Id, FeatEffect Effect)> derived)
		{
			var byClass = derived.GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.ToList());
			var classBase = (await db.ClassAttributeBases.AsNoTracking()
					.Where(b => b.StatId == CharacterStatId.NonCombatManaRegen && b.Level == 1)
					.ToListAsync())
				.ToDictionary(b => b.Class, b => b.Amount);

			Console.WriteLine();
			Console.WriteLine("  ⭐ out-of-combat MANA regen base (stat 280), which nothing sourced before - class base + the auto-trained feat:");
			Console.WriteLine($"     {"class",-16} {"base",6} {"feat",6} {"total",6}  retail");

			// The three retail observations, verbatim from RegenRates' own remark.
			var observed = new Dictionary<ConanClass, int>
			{
				[ConanClass.Assassin] = 0,
				[ConanClass.HeraldOfXotli] = 2000,
				[ConanClass.Necromancer] = 2200,
				[ConanClass.Demonologist] = 2200
			};

			foreach (var (cls, expected) in observed.OrderBy(o => o.Key.ToString(), StringComparer.Ordinal))
			{
				var picked = byClass.TryGetValue(cls, out var reached)
					? AutoTrainedFeatLadder.Select(reached.Select(r => new AutoTrainedFeatLadder.Rung(r.Level, r.Id, r.Effect)))
						.Where(e => e.StatId == CharacterStatId.NonCombatManaRegen).Sum(e => e.Value)
					: 0;
				var bas = classBase.GetValueOrDefault(cls);
				var total = bas + picked;
				Console.WriteLine($"     {cls.ToString(),-16} {bas,6} {picked,6} {total,6}  {(total == expected ? "✅" : "⛔")} captured {expected}");
			}
		}
	}
}
