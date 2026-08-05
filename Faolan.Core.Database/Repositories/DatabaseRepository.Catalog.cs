using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface ICatalogRepository
	{
		Task<Ability[]> GetAbilities();
		Task<Ability?> GetAbility(uint id);
		Task<Ability[]> GetClassAbilities(uint level, ConanClass @class);
		Task<Ability[]> GetClassAbilitiesUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive);

		// The mount abilities a character has EARNED by owning a mount - empty for a character that owns none. Mount
		// abilities hang off ownership, not class or level (#303), and owning a mount is knowing its summon spell
		// (Mount.Id IS the spell id), so this is one join away from the character's spell book.
		Task<Ability[]> GetMountAbilities(uint characterId);
		Task<Combo[]> GetClassCombos(uint level, ConanClass @class);
		Task<Combo[]> GetClassCombosUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<Combo[]> GetCombos();
		Task<ClassComboScaling[]> GetClassComboScalings();
		Task<uint[]> GetClassSpellsUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive);
		Task<Item?> GetItem(uint id);
		Task<uint?> GetItemSearchCategory(uint template);
		Task<Item[]> GetItems(params IReadOnlyList<uint> ids);
		Task<IReadOnlyDictionary<string, MorphTarget>> GetMorphTargetRegistry();
		Task<Mount?> GetMount(uint id);
		Task<Mount[]> GetMounts();
		Task<Item[]> GetRandomBuildableItems(int count);
		Task<Item[]> GetRandomLootExcludedItems();
		Task<Recipe[]> GetRecipes();
		Task<Spell?> GetSpell(uint id);
		Task<SpellEffect[]> GetSpellEffects();
		Task<Spell[]> GetSpells();
		Task<SpellStatEffect[]> GetSpellStatEffects();
		Task<uint[]> GetStartingBarSpells(uint level, ConanClass @class);
		Task<uint[]> GetStartingSpells(uint level, ConanClass @class);
		Task<Dictionary<string, StockPhrase>> GetStockPhrases();
		Task<Item[]> SearchItems(string nameSubstring, int limit = 25);
		Task<bool> SetItemRandomLootExcluded(uint itemId, bool excluded);
	}

	public partial class DatabaseRepository
	{
		private Task<IReadOnlyDictionary<string, MorphTarget>>? _morphTargetRegistry;

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

		public async Task<Ability[]> GetClassAbilities(uint level, ConanClass @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassAbilities.Where(ca => ca.Class == @class && ca.Level <= level).Include(ca => ca.Ability).Select(ca => ca.Ability).OrderBy(a => a.Slot).ToArrayAsync();
		}

		// ⚠️ WHICH mount grants WHICH ability is not modelled, because nothing states it: the two mount owners in the
		// corpus both own a horse AND a mammoth and both hold all six captured mount abilities, so no capture separates
		// "Mammoth Charge comes with the mammoth" from "these six come with owning any mount". Granting the observed
		// set on any ownership is what the evidence supports; per-mount attribution would be invention.
		// [V0: which mount grants which mount ability]
		public async Task<Ability[]> GetMountAbilities(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (!await ctx.CharacterSpells.AnyAsync(cs => cs.CharacterId == characterId && ctx.Mounts.Any(m => m.Id == cs.SpellId)))
				return [];

			var prefix = Ability.MountAbilityNamePrefix;
			return await ctx.Abilities.Where(a => a.Name.StartsWith(prefix)).OrderBy(a => a.Slot).ThenBy(a => a.Id).ToArrayAsync();
		}

		public async Task<Ability[]> GetClassAbilitiesUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive)
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

		public async Task<Combo[]> GetClassCombos(uint level, ConanClass @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassCombos.Where(cc => cc.Class == @class && cc.Level <= level).Include(cc => cc.Combo).Select(cc => cc.Combo).ToArrayAsync();
		}

		public async Task<Combo[]> GetClassCombosUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassCombos.Where(cc => cc.Class == @class && cc.Level > fromLevelExclusive && cc.Level <= toLevelInclusive).Include(cc => cc.Combo).Select(cc => cc.Combo).ToArrayAsync();
		}

		// The whole combo catalog (744 rows). The combo RUNTIME (ComboService) needs to resolve an arbitrary
		// `CommandMessage.combo_id` off the wire - including combos the player's class does not own, which is a real
		// captured case (a Herald performing `Slave_1_AOE_2hb_Sweep_2`) and the NPC combos NPCs swing with - so it
		// caches the catalog whole rather than querying per swing.
		public async Task<Combo[]> GetCombos()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Combos.OrderBy(c => c.Id).ToArrayAsync();
		}

		// The per-class/level combo DPS + stamina curve (RDB 1000513, 12 classes x 80 levels). Cached whole by the
		// combo runtime for the same reason as the catalog above: it is read once per finisher.
		public async Task<ClassComboScaling[]> GetClassComboScalings()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassComboScalings.ToArrayAsync();
		}

		// The class's SPELLBOOK spells newly unlocked by crossing into `toLevelInclusive` - what the level-up path
		// learns (DispatchLearnSpell) and persists.
		//
		// ⚠️ A COMBO is not a spellbook spell (#298). `ClassSpells` is derived from the spell record's class-encoded
		// `GuiStackingLine`, and a melee combo's GameAction has one too, so the map attributes combos to their class
		// exactly like spells - `Combos.GameActionId` IS a `Spells.Id`, and 726 of the 732 combos that carry one
		// resolve. Retail never puts those in the spellbook: across the four captured characters' known-spell lists
		// (246 entries, including a level-80 Necromancer's 109 and a Herald's 127) NOT ONE is a combo GameAction.
		// They arrive on their own path - `GetClassCombosUnlockedBetween` -> ComboBookIIR, right after the NewLevelIIR.
		//
		// Excluding them here is also what explains the per-class feat-grant coverage: the five classes that looked
		// like a total gap (Barbarian, Guardian, Conqueror, Assassin, Ranger - zero teaching feats between them) are
		// 227 of 259 combos, so there was nothing for a feat to teach. What is left is a real, ordinary gap of the
		// same kind the caster classes have.
		public async Task<uint[]> GetClassSpellsUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				.Where(s => s.Level > fromLevelExclusive && s.Level <= toLevelInclusive)
				.Where(s => !ctx.Combos.Any(c => c.GameActionId == s.Id))
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

		public async Task<Item[]> GetItems(params IReadOnlyList<uint> ids)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.Where(i => ids.Contains(i.Id)).ToArrayAsync();
		}

		// The client's character-customization registry, by resource ref. Every player spawn resolves the character's
		// editor choices through it (PlayerSpawnScaffoldRows.BuildWeightedEntries), so it sits on the world-entry and
		// broadcast paths - but it is 1072 rows of STATIC seed content that only the importer ever rewrites, so it is
		// read once and held for the process rather than queried per spawn. Two callers racing the first read is
		// harmless: they build the same dictionary and one of them wins.
		public Task<IReadOnlyDictionary<string, MorphTarget>> GetMorphTargetRegistry()
		{
			return _morphTargetRegistry ??= LoadMorphTargetRegistry();
		}

		private async Task<IReadOnlyDictionary<string, MorphTarget>> LoadMorphTargetRegistry()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.MorphTargets.AsNoTracking().ToDictionaryAsync(t => t.Name);
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

		// One summonable mount by the id the client sends in its SummonMount action (#627). The summon Spell is
		// included because the mount's NAME lives there - a Mount row is its creature id and quality, and the name is
		// the spell's (Mount.Id IS the spell id).
		public async Task<Mount?> GetMount(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Mounts.Include(m => m.Spell).FirstOrDefaultAsync(m => m.Id == id);
		}

		public async Task<Mount[]> GetMounts()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Mounts.Include(m => m.Spell).OrderBy(m => m.Id).ToArrayAsync();
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

		// The server-acted spell effects (pet summons, triggered procs). Read whole in run order - a few dozen rows,
		// cached in memory by SpellEffectService, and the lookup sits on the cast-resolve path.
		public async Task<SpellEffect[]> GetSpellEffects()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellEffects.OrderBy(e => e.SpellId).ThenBy(e => e.Trigger).ThenBy(e => e.Sequence).ToArrayAsync();
		}

		// Every spell's [2][9] stat effects (#367). Read whole - it is 1.7k rows over 866 spells, cached in memory by
		// BuffStatEffects, and a per-spell query on the buff-apply path would hit the DB on every cast.
		public async Task<SpellStatEffect[]> GetSpellStatEffects()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellStatEffects.OrderBy(e => e.SpellId).ThenBy(e => e.StatId).ToArrayAsync();
		}

		public async Task<uint[]> GetStartingBarSpells(uint level, ConanClass @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				.Where(s => s.Level > 0 && s.Level <= level)
				.Where(s => !ctx.Combos.Any(c => c.GameActionId == s.Id)) // a combo is not a spellbook spell - see GetClassSpellsUnlockedBetween (#298)
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
		//
		// Above level 1 the gate prefers ClassSpell.GrantLevel - the level a capture SHOWS this class being granted
		// this spell (#376) - and falls back to Spells.Level only where no capture has caught it. The two are
		// different facts, not rival measurements: retail grants at or BELOW the RDB field, never above (Desecrating
		// Essence R1 granted at 3 vs field 5; Contract of Protection R1 at 5 vs 10), so the field is a trainer /
		// full-power level and the capture is the unlock. Only a handful of GrantLevels exist so far; every gameplay
		// capture containing a ding adds more, and unlike a scrape they cannot be wrong about what retail granted.
		public async Task<uint[]> GetStartingSpells(uint level, ConanClass @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => new
				{
					cs,
					s
				})
				// The LEVEL branch excludes combos - a combo is not a spellbook spell, see GetClassSpellsUnlockedBetween
				// (#298). The starter kit is left alone: it is a captured LIST, so whatever it holds is what retail
				// granted (and in fact holds no combo GameAction).
				.Where(x => x.cs.IsStarter || (level > 1 && !ctx.Combos.Any(c => c.GameActionId == x.s.Id) && (x.cs.GrantLevel != null
					? x.cs.GrantLevel <= level
					: x.s.Level > 1 && x.s.Level <= level)))
				.OrderBy(x => x.s.Level)
				.ThenBy(x => x.s.Id)
				.Select(x => x.s.Id)
				.Distinct()
				.ToArrayAsync();
		}

		// Returns the ROWS, not bytes: the 0x205D payload is rebuilt from their columns by RemoteFormatXml
		// (Faolan.Core.Network), which this layer cannot reference - Network depends on Database, not the reverse.
		public async Task<Dictionary<string, StockPhrase>> GetStockPhrases()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.StockPhrases.ToDictionaryAsync(p => p.Key, p => p);
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
	}
}
