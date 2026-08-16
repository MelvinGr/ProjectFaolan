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
		Task<Recipe?> GetRecipe(uint id);
		Task<Recipe[]> GetRecipes();
		Task<Spell?> GetSpell(uint id);
		Task<uint[]> SpellIdsOnLine(uint guiStackingLine);

		// #764: every template a consumable's SHARED cooldown locks - the items on this line plus the spells they cast.
		Task<uint[]> LockedTemplateIdsForConsumableLine(uint lockSpellLine);
		Task<SpellEffect[]> GetSpellEffects();
		Task<SpellAreaEffect[]> GetSpellAreaEffects();
		Task<SpellSpellStatModifier[]> GetSpellStatModifiers();
		Task<(uint Line, uint SpellId)[]> GetSpellLineMembers();
		Task<Spell[]> GetSpells();
		Task<SpellStatEffect[]> GetSpellStatEffects();
		Task<ILookup<(uint SpellId, uint Sequence), SpellStatEffectCriterion>> GetSpellStatEffectCriteria();
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
		//
		// ⚠️ AN EFFECT IS NOT A SPELLBOOK SPELL EITHER, and it reaches `ClassSpells` by the same route a combo does:
		// the DoT a spell APPLIES carries its caster's class-encoded `GuiStackingLine`, so the map attributes it to
		// the class exactly like the spell that applies it. `Infestation` is what `Flesh to Worms` inflicts - the
		// client's own description says so - and all five of its ranks sit at Level 2 because an effect's ranks pair
		// with the CASTER's ranks rather than with a level ladder of their own. So a single level 1 -> 2 ding put
		// `Infestation (Rank 1..5)` in a Necromancer's book, five spells retail never grants at all.
		//
		// The oracle is the same one the combo rule used: the captured L80 Necromancer knows all five ranks of
		// `Flesh to Worms` (2/25/40/55/70) and ZERO ranks of `Infestation`.
		//
		// ⚠️ The rule is NOT "anything named as some spell's EffectSpell" - that is the obvious form and it is wrong.
		// `Flesh to Worms (Rank 1)` is itself named as the effect of `Plague Swarm`, so the naive predicate deletes
		// the very spell the level-2 ding is supposed to teach. The referrer has to be a real BOOK spell for its
		// target to be an effect: `Plague Swarm` is Level 0 and so grants nothing, while `Flesh to Worms` is a
		// leveled `ClassSpells` entry and so its target genuinely is one.
		//
		// Measured over all four captured characters' known-spell lists: the rule removes 67 over-grants (38 from the
		// L80 Necromancer, 19 from the L80 Demonologist, 10 from the Herald) at **zero** false positives - it never
		// drops a spell a captured character actually knows.
		//
		// ⚠️ This does NOT close the over-grant, and is not claimed to: the same four characters show the level path
		// offering 268/220/43 spells where retail gave them 91/100/4, so effects are one identified band of a much
		// larger gap. Tracked in Gitea #847 - do not read a passing spellbook as a solved one.
		//
		// ⚠️ The GATE is `ClassSpell.GrantLevel` where a capture has caught the grant and `Spells.Level` everywhere
		// else - the SAME expression `GetStartingSpells` gates on, and it has to be, because the two answer one
		// question between them: this one decides what a DING teaches and that one what a RELOG restores. They used
		// to disagree, this one reading `Spells.Level` alone, and every row where the two facts differ was a spell
		// the login path said a character of that level knows and the ding path taught at a different level entirely
		// (`Runed Flesh (Rank 1)` granted at 5, requirement 10; `Contract of Protection (Rank 1)` at 5 vs 10) - or
		// never, for the two `Dance:` emotes whose requirement is 0 and whose captured grant is at 5.
		public async Task<uint[]> GetClassSpellsUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => new
				{
					cs,
					s,
					Gate = (uint)(cs.GrantLevel ?? s.Level)
				})
				.Where(x => x.Gate > fromLevelExclusive && x.Gate <= toLevelInclusive)
				.Where(x => !ctx.Combos.Any(c => c.GameActionId == x.s.Id))
				.Where(x => !ctx.Spells.Any(applier => applier.EffectSpell == x.s.Id && applier.Level > 0 && ctx.ClassSpells.Any(cs => cs.SpellId == applier.Id)))
				.OrderBy(x => x.Gate)
				.ThenBy(x => x.s.Id)
				.Select(x => x.s.Id)
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

		// "Buildable" no longer means "has a harvested hash": since #723 every item HAS a wire token, harvested or
		// minted (AcgHash.ForWire), so the `Hash != 0` filter that used to sit here would only exclude the 232
		// templates no capture happened to show entering an inventory - which is a gap in our harvest, not a
		// property of the item. A NAME is still required: a nameless row has no identity to render.
		public async Task<Item[]> GetRandomBuildableItems(int count)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Items.Where(i => i.Name != "" && i.Type == ItemType.Normal && !i.ExcludeFromRandomLoot).OrderBy(_ => EF.Functions.Random()).Take(count).ToArrayAsync();
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

		// One recipe by the id the client's craft request names - the same id `DispatchTradeskillComplete` echoes back.
		// The whole book is 603 rows with two Includes, so the craft path takes this rather than filtering GetRecipes.
		public async Task<Recipe?> GetRecipe(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Recipes.Include(r => r.Items).ThenInclude(i => i.Item).FirstOrDefaultAsync(r => r.Id == id);
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

		// Every spell sharing a GUI stacking LINE - the group a line-scoped recast covers, which retail locks one
		// message per member of (#767: a `Contract of Protection` cast emits five `DispatchLockTemplate` bodies, one
		// per rank of line 12). Ordered by id so the emitted sequence is stable rather than storage-dependent.
		public async Task<uint[]> SpellIdsOnLine(uint guiStackingLine)
		{
			if (guiStackingLine == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Spells.AsNoTracking().Where(s => s.GuiStackingLine == guiStackingLine).OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync();
		}

		// #764: every template a consumable's SHARED cooldown covers - the items on `OnUseLockSpellLine` and the on-use
		// spells they cast. That set comes from the client's own item templates rather than from a line catalog: a
		// `CoreLockSpellLineTemplate` names a LINE (health 80000 / stamina 80100 / mana 80200), and the line's
		// membership is exactly which items declare it, which is why no line table is needed.
		//
		// The spells are included because that is what retail locks. In `NecroL80_..._login_use-consumables`, using the
		// firework item 3765539 produces THREE `DispatchLockTemplate` bodies - that item template and both spells it
		// casts (3510737 / 3510738) - so a consumable's lock covers the thing on the bar AND what it triggers.
		//
		// Ordered so the emitted sequence is stable rather than storage-dependent.
		public async Task<uint[]> LockedTemplateIdsForConsumableLine(uint lockSpellLine)
		{
			if (lockSpellLine == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var members = await ctx
				.Items.AsNoTracking()
				.Where(i => i.OnUseLockSpellLine == lockSpellLine)
				.Select(i => new
				{
					i.Id,
					i.OnUseSpell
				})
				.ToListAsync();

			return members
				.Select(m => m.Id)
				.Concat(members.Where(m => m.OnUseSpell is > 0).Select(m => m.OnUseSpell!.Value))
				.Distinct()
				.Order()
				.ToArray();
		}

		// #772: every spell's AREA applications, read whole - a few thousand rows the importer owns, cached by
		// SpellAreaCatalog because both consumers (the cast resolve and the world tick) are hot paths.
		public async Task<SpellAreaEffect[]> GetSpellAreaEffects()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellAreaEffects.AsNoTracking().OrderBy(a => a.SpellId).ThenBy(a => a.Sequence).ToArrayAsync();
		}

		// #710: every spell-scoped stat modifier a SPELL carries, read whole - 1,571 rows the importer owns, cached by
		// SpellModifierService because the lookup sits on the cast-resolve path.
		public async Task<SpellSpellStatModifier[]> GetSpellStatModifiers()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellSpellStatModifiers.AsNoTracking().OrderBy(m => m.SpellId).ThenBy(m => m.Sequence).ToArrayAsync();
		}

		// The spell LINE -> its member spells map (#710). A line-scoped modifier covers every RANK of the line at
		// once, so resolving one needs this fan-out; done as a single catalog read cached beside the modifiers rather
		// than a join per cast.
		public async Task<(uint Line, uint SpellId)[]> GetSpellLineMembers()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Spells.AsNoTracking()
				.Where(s => s.GuiStackingLine > 0)
				.Select(s => new ValueTuple<uint, uint>(s.GuiStackingLine, s.Id))
				.ToArrayAsync();
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

		// #791: the CONDITIONS on each effect, keyed the way the effect is, with the run in `Pos` order - the run is a
		// postfix expression, so the order is part of the data.
		public async Task<ILookup<(uint SpellId, uint Sequence), SpellStatEffectCriterion>> GetSpellStatEffectCriteria()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx.SpellStatEffectCriteria.OrderBy(c => c.SpellId).ThenBy(c => c.Sequence).ThenBy(c => c.Pos).ToArrayAsync();
			return rows.ToLookup(c => (c.SpellId, c.Sequence));
		}

		public async Task<uint[]> GetStartingBarSpells(uint level, ConanClass @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				.Where(s => s.Level > 0 && s.Level <= level)
				.Where(s => !ctx.Combos.Any(c => c.GameActionId == s.Id)) // a combo is not a spellbook spell - see GetClassSpellsUnlockedBetween (#298)
				// ...and neither is an EFFECT: the DoT a book spell applies carries its caster's class-encoded
				// stacking line, so `ClassSpells` attributes it to the class exactly like a combo. Same rule and same
				// reason as the ding path - see GetClassSpellsUnlockedBetween, including why the referrer must itself
				// be a book spell. Without it the relog re-grant put back what the ding no longer teaches.
				.Where(s => !ctx.Spells.Any(applier => applier.EffectSpell == s.Id && applier.Level > 0 && ctx.ClassSpells.Any(cs => cs.SpellId == applier.Id)))
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
				// The LEVEL branch excludes combos AND effects - neither is a spellbook spell, see
				// GetClassSpellsUnlockedBetween (#298 / #847). The starter kit is left alone in both cases: it is a
				// captured LIST, so whatever it holds is what retail granted (and in fact holds no combo GameAction).
				.Where(x => x.cs.IsStarter || (level > 1 && !ctx.Combos.Any(c => c.GameActionId == x.s.Id)
					&& !ctx.Spells.Any(applier => applier.EffectSpell == x.s.Id && applier.Level > 0 && ctx.ClassSpells.Any(cs => cs.SpellId == applier.Id))
					&& (x.cs.GrantLevel != null
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
