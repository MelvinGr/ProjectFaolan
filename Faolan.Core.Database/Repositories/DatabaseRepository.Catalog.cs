using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public partial interface IDatabaseRepository
	{
		Task<Ability[]> GetAbilities();
		Task<Ability?> GetAbility(uint id);
		Task<Ability[]> GetClassAbilities(uint level, Class @class);
		Task<Ability[]> GetClassAbilitiesUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<Combo[]> GetClassCombos(uint level, Class @class);
		Task<Combo[]> GetClassCombosUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<uint[]> GetClassSpellsUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<Item?> GetItem(uint id);
		Task<List<Item>> GetItems(params IReadOnlyList<uint> ids);
		Task<uint?> GetItemSearchCategory(uint template);
		Task<Dictionary<string, MorphTarget>> GetMorphTargets(IReadOnlyCollection<string> names);
		Task<Item[]> GetRandomBuildableItems(int count);
		Task<Item[]> GetRandomLootExcludedItems();
		Task<Recipe[]> GetRecipes();
		Task<Spell?> GetSpell(uint id);
		Task<Spell[]> GetSpells();
		Task<uint[]> GetStartingBarSpells(uint level, Class @class);
		Task<uint[]> GetStartingSpells(uint level, Class @class);
		Task<Dictionary<string, byte[]>> GetStockPhrases();
		Task<Item[]> SearchItems(string nameSubstring, int limit = 25);
		Task<bool> SetItemRandomLootExcluded(uint itemId, bool excluded);
	}

	public partial class DatabaseRepository
	{
		// Interior lowercase words allowed inside a combo DISPLAY name (e.g. "Burn to Death", "Body and Spirit Wrack").
		private static readonly HashSet<string> ComboNameConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"to",
			"of",
			"and",
			"the",
			"for",
			"a",
			"in",
			"on",
			"from",
			"with",
			"your",
			"our",
			"my"
		};

		public async Task<Ability[]> GetAbilities()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Abilities.OrderBy(a => a.Id).ToArrayAsync();
		}

		public async Task<Ability?> GetAbility(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Abilities.FirstOrDefaultAsync(a => a.Id == id);
		}

		public async Task<Ability[]> GetClassAbilities(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassAbilities.Where(ca => ca.Class == @class && ca.Level <= level).Include(ca => ca.Ability).Select(ca => ca.Ability).OrderBy(a => a.Slot).ToArrayAsync();
		}

		public async Task<Ability[]> GetClassAbilitiesUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassAbilities.Where(ca => ca.Class == @class && ca.Level > fromLevelExclusive && ca.Level <= toLevelInclusive)
				.Include(ca => ca.Ability)
				.OrderBy(ca => ca.Level)
				.ThenBy(ca => ca.AbilityId)
				.Select(ca => ca.Ability)
				.ToArrayAsync();
		}

		public async Task<Combo[]> GetClassCombos(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx.ClassCombos.Where(cc => cc.Class == @class && cc.Level <= level).Include(cc => cc.Combo).Select(cc => cc.Combo).ToListAsync();
			return HighestRankPerFamily(candidates);
		}

		public async Task<Combo[]> GetClassCombosUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx
				.ClassCombos.Where(cc => cc.Class == @class && cc.Level > fromLevelExclusive && cc.Level <= toLevelInclusive)
				.Include(cc => cc.Combo)
				.Select(cc => cc.Combo)
				.ToListAsync();

			return HighestRankPerFamily(candidates);
		}

		public async Task<uint[]> GetClassSpellsUnlockedBetween(Class @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				.Where(s => s.Level > fromLevelExclusive && s.Level <= toLevelInclusive)
				.OrderBy(s => s.Level)
				.ThenBy(s => s.Id)
				.Select(s => s.Id)
				.Distinct()
				.ToArrayAsync();
		}

		public async Task<Item?> GetItem(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.FirstOrDefaultAsync(i => i.Id == id);
		}

		public async Task<List<Item>> GetItems(params IReadOnlyList<uint> ids)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.Where(i => ids.Contains(i.Id)).ToListAsync();
		}

		public async Task<uint?> GetItemSearchCategory(uint template)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var item = await ctx.Items.FirstOrDefaultAsync(i => i.Id == template);
			if (item is { SearchCategory: > 0 })
				return item.SearchCategory;

			var categories = await ctx.TradepostListings.Where(l => l.Template == template && l.ItemId != l.Template).Select(l => l.ItemId).ToListAsync();
			if (categories.Count == 0)
				return null;

			return categories.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
		}

		public async Task<Dictionary<string, MorphTarget>> GetMorphTargets(IReadOnlyCollection<string> names)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.MorphTargets.Where(t => names.Contains(t.Name)).ToDictionaryAsync(t => t.Name);
		}

		public async Task<Item[]> GetRandomBuildableItems(int count)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.Where(i => i.Hash != 0 && i.Name != "" && i.Type == ItemType.Normal && !i.ExcludeFromRandomLoot).OrderBy(_ => EF.Functions.Random()).Take(count).ToArrayAsync();
		}

		public async Task<Item[]> GetRandomLootExcludedItems()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.Where(i => i.ExcludeFromRandomLoot && i.Type == ItemType.Normal).OrderBy(i => i.Name).ToArrayAsync();
		}

		public async Task<Recipe[]> GetRecipes()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Recipes.Include(r => r.Items).ThenInclude(i => i.Item).OrderBy(r => r.Id).ToArrayAsync();
		}

		public async Task<Spell?> GetSpell(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Spells.FirstOrDefaultAsync(s => s.Id == id);
		}

		public async Task<Spell[]> GetSpells()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Spells.OrderBy(s => s.Id).ToArrayAsync();
		}

		public async Task<uint[]> GetStartingBarSpells(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				.Where(s => s.Level > 0 && s.Level <= level)
				.Select(s => new
				{
					s.Id,
					s.Name,
					s.Level
				})
				.ToListAsync();

			return candidates
				.GroupBy(s => StripRankSuffix(s.Name))
				.Select(g => new
				{
					BestId = g.OrderByDescending(s => s.Level).ThenByDescending(s => s.Id).First().Id,
					FirstLevel = g.Min(s => s.Level)
				})
				.OrderBy(x => x.FirstLevel)
				.ThenBy(x => x.BestId)
				.Select(x => x.BestId)
				.ToArray();

			// The spell-family key: the name with a trailing " (Rank N)" removed (and trailing spaces trimmed - a few
			// catalog names carry a double space before the rank, e.g. "Freeze  (Rank 2)").
			static string StripRankSuffix(string name)
			{
				if (string.IsNullOrEmpty(name))
					return name ?? "";

				var i = name.IndexOf(" (Rank ", StringComparison.Ordinal);
				return (i >= 0
					? name[..i]
					: name).TrimEnd();
			}
		}

		// The spells a character of this class/level should already know: the create-granted STARTER KIT, plus (above
		// level 1) everything its class has access to whose level requirement it meets.
		//
		// LEVEL 1 IS THE KIT'S EXCLUSIVE DOMAIN, and that is capture-proven rather than a tidy-up: a create capture
		// shows retail granting the necro EXACTLY Ice Strike (Rank 1) + Frozen Hatred. A plain "Level <= 1" query
		// returns six - the class also has access to four other spells the RDB rates level 1 (Vile Insight,
		// Pestilential Blast R1, ...) that retail demonstrably does NOT grant at create. So the create kit is a fixed
		// LIST, not "every level-1 spell", and the level query starts at 2.
		//
		// Conversely the kit ignores the requirement entirely - retail grants Ice Strike (Rank 1) at creation though
		// the RDB rates it level 7 (Hellfire Breath R1: level 25). Level never reaches the client (DispatchLearnSpell
		// 0x200A carries the spell id alone), so it is purely this gate; the client's own view comes from its RDB.
		// This used to be faked by PINNING the kit to Level 1 and pushing the four others to 2, which overwrote real
		// levels with sentinels. See ClassSpell.IsStarter.
		public async Task<uint[]> GetStartingSpells(uint level, Class @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => new { cs, s })
				.Where(x => x.cs.IsStarter || (level > 1 && x.s.Level > 1 && x.s.Level <= level))
				.OrderBy(x => x.s.Level)
				.ThenBy(x => x.s.Id)
				.Select(x => x.s.Id)
				.Distinct()
				.ToArrayAsync();
		}

		public async Task<Dictionary<string, byte[]>> GetStockPhrases()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.StockPhrases.ToDictionaryAsync(p => p.Key, p => p.RemoteFormat);
		}

		public async Task<Item[]> SearchItems(string nameSubstring, int limit = 25)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = nameSubstring.ToLowerInvariant();
			return await ctx.Items.Where(i => i.Name.ToLower().Contains(lowered)).OrderBy(i => i.Name).Take(limit).ToArrayAsync();
		}

		public async Task<bool> SetItemRandomLootExcluded(uint itemId, bool excluded)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExcludeFromRandomLoot, excluded)) == 1;
		}

		// Collapses a combo set to one entry per family - the highest rank (by Level, then Id) - ordered low->high by
		// first-acquired level. The combo twin of the spell-bar family collapse in GetStartingBarSpells.
		private static Combo[] HighestRankPerFamily(IEnumerable<Combo> combos)
		{
			return combos.GroupBy(c => ComboFamilyKey(c.Name)).Select(g => g.OrderByDescending(c => c.Level).ThenByDescending(c => c.Id).First()).OrderBy(c => c.Level).ThenBy(c => c.Id).ToArray();

			// The combo-family key: groups the ranks of one combo LINE (Hell Strikes I/II/III...) so the grant can pick
			// just the highest rank the character qualifies for - the combo analogue of StripRankSuffix for spells. Combo
			// catalog names are ENGINE names ({Class}_{level}_{tags}_{Display Name}_{RankRoman}, e.g.
			// Herald_05_multihit_Hell_Strikes_I), so the family is the trailing DISPLAY-name run (Capitalized words +
			// interior connectors like "to"/"and"), normalised (lowercased, separators stripped) after dropping the class
			// prefix and a trailing roman-numeral rank. This groups I..V of a line regardless of the differing level/tag
			// prefix, and merges the "80b" / space-in-name / "khitai" variants that share a display name (validated in
			// Python against the Herald catalog: a lvl-24 HoX collapses to the 5 the retail 0x2076 book shows).
			static string ComboFamilyKey(string name)
			{
				if (string.IsNullOrEmpty(name))
					return name ?? "";

				var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
				if (tokens.Length <= 1)
					return name.ToLowerInvariant();

				var end = tokens.Length;
				if (IsRomanNumeral(tokens[end - 1]))
					end--;

				// Walk back over the trailing display run (stop before the class prefix at index 0).
				var start = end;
				for (var i = end - 1; i >= 1; i--)
				{
					var t = tokens[i];
					if (t.Length > 0 && (char.IsUpper(t[0]) || ComboNameConnectors.Contains(t)))
						start = i;
					else
						break;
				}

				if (start >= end)
					return name.ToLowerInvariant().Replace("_", "").Replace(" ", "");

				return string.Concat(tokens[start..end]).ToLowerInvariant().Replace(" ", "");
			}

			// True for a combo rank token (I, II, III, IV, V, VI, VII, ...): uppercase roman numeral characters only.
			static bool IsRomanNumeral(string s)
			{
				if (string.IsNullOrEmpty(s) || s.Length > 5)
					return false;

				foreach (var c in s)
				{
					if (c is not ('I' or 'V' or 'X'))
						return false;
				}

				return true;
			}
		}
	}
}
