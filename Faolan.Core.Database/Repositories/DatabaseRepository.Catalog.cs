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

		/// <summary>
		///     The mount abilities a character has earned by owning a mount - empty for a character that owns none.
		/// </summary>
		/// <remarks>
		///     Mount abilities hang off ownership, not class or level, and owning a mount is knowing its summon spell
		///     (<c>Mount.Id</c> is the spell id), so this is one join away from the character's spell book.
		/// </remarks>
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

		/// <summary>
		///     Every template a consumable's shared cooldown locks - the items on this line plus the spells they cast.
		/// </summary>
		Task<uint[]> LockedTemplateIdsForConsumableLine(uint lockSpellLine);
		Task<SpellEffect[]> GetSpellEffects();
		Task<SpellAreaEffect[]> GetSpellAreaEffects();
		Task<SpellAreaEffectCriterion[]> GetSpellAreaEffectCriteria();
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

		/// <remarks>
		///     Which mount grants which ability is not modelled, because nothing states it: the two mount owners in the
		///     corpus both own a horse and a mammoth and both hold all six captured mount abilities, so no capture
		///     separates "Mammoth Charge comes with the mammoth" from "these six come with owning any mount". Granting
		///     the observed set on any ownership is what the evidence supports; per-mount attribution would be invention.
		///     [V0: which mount grants which mount ability]
		/// </remarks>
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

		/// <summary>
		///     The whole combo catalog (744 rows). The combo runtime (<c>ComboService</c>) needs to resolve an arbitrary
		///     <c>CommandMessage.combo_id</c> off the wire - including combos the player's class does not own, which is a
		///     real captured case (a Herald performing <c>Slave_1_AOE_2hb_Sweep_2</c>) and the NPC combos NPCs swing with
		///     - so it caches the catalog whole rather than querying per swing.
		/// </summary>
		public async Task<Combo[]> GetCombos()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Combos.OrderBy(c => c.Id).ToArrayAsync();
		}

		/// <summary>
		///     The per-class/level combo DPS + stamina curve (RDB 1000513, 12 classes x 80 levels). Cached whole by the
		///     combo runtime for the same reason as the catalog above: it is read once per finisher.
		/// </summary>
		public async Task<ClassComboScaling[]> GetClassComboScalings()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassComboScalings.ToArrayAsync();
		}

		/// <summary>
		///     The class's spellbook spells newly unlocked by crossing into <paramref name="toLevelInclusive" /> - what
		///     the level-up path learns (<c>DispatchLearnSpell</c>) and persists.
		/// </summary>
		/// <remarks>
		///     A combo is not a spellbook spell. <c>ClassSpells</c> is derived from the spell record's class-encoded
		///     <c>GuiStackingLine</c>, and a melee combo's GameAction has one too, so the map attributes combos to their
		///     class exactly like spells - <c>Combos.GameActionId</c> is a <c>Spells.Id</c>, and 726 of the 732 combos
		///     that carry one resolve. Retail never puts those in the spellbook: across the four captured characters'
		///     known-spell lists (246 entries, including a level-80 Necromancer's 109 and a Herald's 127) not one is a
		///     combo GameAction. They arrive on their own path - <c>GetClassCombosUnlockedBetween</c> into ComboBookIIR,
		///     right after the NewLevelIIR.
		///     <para>
		///         Excluding them here is also what explains the per-class feat-grant coverage: the five classes that
		///         look like a total gap (Barbarian, Guardian, Conqueror, Assassin, Ranger - zero teaching feats between
		///         them) are 227 of 259 combos, so there is nothing for a feat to teach. What is left is a real, ordinary
		///         gap of the same kind the caster classes have.
		///     </para>
		///     <para>
		///         Nor is a spell a combo chains. <c>SpellAreaEffects</c> with kind <c>CastNano</c> is a spell-to-spell
		///         apply link, and a combo's GameAction uses it to inflict its debuff - which then carries the caster's
		///         class-encoded stacking line and lands in <c>ClassSpells</c> exactly like the DoT below. It is the same
		///         mistake one link over, and it is what the melee classes' whole "spellbook" otherwise is:
		///         <c>Staggering Blow I-IV</c> (Barbarian), <c>Lethargy I-III</c> (Assassin), <c>Bleeding Puncture I-IV</c>
		///         (Ranger), <c>Disable I-III</c> (Guardian). The rule takes 29 of the 68 the five of them were offered,
		///         all four of the Barbarian's and all three of the Assassin's. Across the 249 spells the five imported
		///         characters know, not one is chained by a combo.
		///     </para>
		///     <para>
		///         The broad form - "chained by any spell" - is measured and rejected: it takes six of the level-80
		///         Necromancer's own 109. Only the combo chainer is a statement about the target.
		///     </para>
		///     <para>
		///         It legitimately empties the Barbarian's and the Assassin's spell list, and that is not a class losing
		///         its kit: they hold 103 and 75 combos, which travel the combo book. A pure-combo class having no
		///         spellbook spells is the shape of the class, not a gap - see
		///         <c>EveryClass_ReachesLevel80WithSomethingToCast</c>.
		///     </para>
		///     <para>
		///         An effect is not a spellbook spell either, and it reaches <c>ClassSpells</c> by the same route a combo
		///         does: the DoT a spell applies carries its caster's class-encoded <c>GuiStackingLine</c>, so the map
		///         attributes it to the class exactly like the spell that applies it. <c>Infestation</c> is what
		///         <c>Flesh to Worms</c> inflicts - the client's own description says so - and all five of its ranks sit
		///         at Level 2 because an effect's ranks pair with the caster's ranks rather than with a level ladder of
		///         their own, so a single level 1 to 2 ding would put <c>Infestation (Rank 1..5)</c> in a Necromancer's
		///         book, five spells retail never grants at all. The oracle is the combo rule's: the captured L80
		///         Necromancer knows all five ranks of <c>Flesh to Worms</c> (2/25/40/55/70) and zero ranks of
		///         <c>Infestation</c>.
		///     </para>
		///     <para>
		///         The rule is not "anything named as some spell's EffectSpell" - that is the obvious form and it is
		///         wrong. <c>Flesh to Worms (Rank 1)</c> is itself named as the effect of <c>Plague Swarm</c>, so the
		///         naive predicate deletes the very spell the level-2 ding is supposed to teach. The referrer has to be a
		///         real book spell for its target to be an effect: <c>Plague Swarm</c> is Level 0 and so grants nothing,
		///         while <c>Flesh to Worms</c> is a leveled <c>ClassSpells</c> entry and so its target genuinely is one.
		///         Measured over all four captured characters' known-spell lists, the rule removes 67 over-grants (38
		///         from the L80 Necromancer, 19 from the L80 Demonologist, 10 from the Herald) at zero false positives.
		///     </para>
		///     <para>
		///         Effects are one band of four; on their own they leave the level path offering 268/220/43 spells where
		///         retail gave those characters 91/100/4. The other three are combos (above), the per-rank level ladder
		///         (<c>RdbSpellImporter.RankLadderFromEffectBands</c>, where field 125 states none and one ding would
		///         teach a whole family) and the feat tree (<c>FeatMapCovers</c>). With all four the offer is 88/95/5
		///         against a known 109/127/8 and zero false positives - <c>--report spell-grant-check</c> is the standing
		///         measurement.
		///     </para>
		///     <para>
		///         The opposite error is a different, open problem: the class-to-spell harvest does not know about whole
		///         bands the captured level-80s hold - the <c>Undead Minion:</c> / <c>Deathless Acolyte:</c> pet lines,
		///         mounts, companions, and the <c>Dance:</c> emotes, which are race-gated rather than class-gated. A
		///         passing spellbook is not a solved one. Open work tracked in Gitea #851 and #852.
		///     </para>
		///     <para>
		///         The gate is <c>ClassSpell.GrantLevel</c> where a capture has caught the grant and <c>Spells.Level</c>
		///         everywhere else - the same expression <c>GetStartingSpells</c> gates on, and it has to be, because the
		///         two answer one question between them: this one decides what a ding teaches and that one what a relog
		///         restores. Where the two facts differ, gating on <c>Spells.Level</c> alone makes the login path say a
		///         character of that level knows a spell the ding path teaches at a different level entirely
		///         (<c>Runed Flesh (Rank 1)</c> granted at 5, requirement 10; <c>Contract of Protection (Rank 1)</c> at 5
		///         vs 10) - or never, for the two <c>Dance:</c> emotes whose requirement is 0 and whose captured grant is
		///         at 5.
		///     </para>
		///     <para>
		///         And the spell has to be one a feat teaches, for a class the feat map speaks for - see
		///         <c>FeatMapCovers</c>.
		///     </para>
		/// </remarks>
		public async Task<uint[]> GetClassSpellsUnlockedBetween(ConanClass @class, uint fromLevelExclusive, uint toLevelInclusive)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var featGated = await FeatMapCovers(ctx, @class);
			var increments = await StackingIncrements(ctx); // a rank table's members are not book spells
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
				.Where(x => !ctx.SpellAreaEffects.Any(chain => chain.EffectSpellId == x.s.Id && ctx.Combos.Any(c => c.GameActionId == chain.SpellId)))
				.Where(x => !increments.Contains(x.s.Id))
				.Where(x => !featGated || ctx.FeatSpellGrants.Any(g => g.SpellId == x.s.Id && g.Feat.TemplateCategory == Feat.AutomaticUnlockCategory))
				.OrderBy(x => x.Gate)
				.ThenBy(x => x.s.Id)
				.Select(x => x.s.Id)
				.Distinct()
				.ToArrayAsync();
		}

		/// <summary>
		///     The spells that are a stacking increment of a rank table, not book spells in their own right.
		/// </summary>
		/// <remarks>
		///     A rank table is a spell in no stacking line carrying more than one <c>CastNano</c> edge into one line -
		///     the shape that puts all ten ranks of <c>Furious Inspiration</c> on the bar at once. <c>Colossus of War</c>
		///     is the same thing one link over: ids 3275545/3275640, <c>GuiStackingLine</c> 0, chaining twenty members of
		///     line 22200, which the level path would otherwise offer a Conqueror as twenty spells.
		///     <para />
		///     Structural, not a name match: a rule keyed on the <c>(N)</c> suffix is the kind of string heuristic this
		///     codebase has repeatedly had to unwind.
		///     <para />
		///     Checked against the same oracle the combo and effect exclusions use, the captured known-spell lists: of
		///     the <b>383</b> spells this catches, <b>zero</b> appear in any captured spellbook, while 210 of them are
		///     currently offered by the level path.
		///     <para />
		///     The carrier must be in no stacking line, and that narrowing is load-bearing rather than tidy. Without it
		///     the rule also catches <c>Bleeding Puncture I..IV</c> (levels 5/36/52/64), which read as an ordinary
		///     learnable line - a carrier that is itself in a line is a rank ladder rung, not a table header. It is also
		///     what keeps <c>Flesh to Worms (Rank 1)</c> safe: the spell a level-2 Necromancer ding exists to teach must
		///     not be deleted by "anything some spell points at".
		///     <para />
		///     The oracle covers four characters of four classes, and no Conqueror among them - so this is supported by
		///     an absence rather than by a positive Conqueror reading. What makes that acceptable is that the rule is
		///     structural and its false-positive candidate was found and excluded by narrowing, not by listing.
		/// </remarks>
		// <c>internal</c> so the suite can gate the set itself rather than only its consequences: the combo rules already
		// exclude some of the same spells, so an observable-effect test cannot tell this rule's narrowing from theirs.
		internal static async Task<HashSet<uint>> StackingIncrements(DatabaseContext ctx)
		{
			// Materialised rather than expressed as a nested EXISTS in each caller: the predicate is a
			// count-per-(carrier, line), which does not translate cleanly, and the set is a few hundred ids.
			var edges = await (from chain in ctx.SpellAreaEffects.Where(e => e.Kind == SpellAreaEffect.CastNanoKind)
				join target in ctx.Spells on chain.EffectSpellId equals target.Id
				join carrier in ctx.Spells on chain.SpellId equals carrier.Id
				where target.GuiStackingLine != 0 && carrier.GuiStackingLine == 0
				select new
				{
					Carrier = carrier.Id,
					target.GuiStackingLine,
					Target = target.Id
				}).ToListAsync();

			return edges.GroupBy(e => new
				{
					e.Carrier,
					e.GuiStackingLine
				})
				.Where(g => g.Select(e => e.Target).Distinct().Count() > 1)
				.SelectMany(g => g.Select(e => e.Target))
				.ToHashSet();
		}

		/// <summary>
		///     Whether the client's feat-to-spell grant map is a statement about this class, and so may be used as the
		///     gate on what its level path teaches. True for the seven classes it grants anything to; false for the five
		///     it grants nothing to, where its silence is missing data rather than "this class learns nothing".
		/// </summary>
		/// <remarks>
		///     The client grants a spell rank through a feat: a <c>&lt;Class&gt; Class Feat NNN</c> for rank 1 and a
		///     <c>Spell Unlocking &lt;lo&gt;-&lt;hi&gt;</c> for ranks 2 and up, whose name carries the level band it
		///     covers. Read as a gate on the level path it closes most of the over-grant, measured against the two
		///     captured level-80s:
		///     <list type="table">
		///         <item>Necromancer: 230 offered to <b>110</b>, against the 109 the captured L80 actually knows.</item>
		///         <item>Demonologist: 201 offered to <b>108</b> (the L80 knows 127).</item>
		///     </list>
		///     and it costs nothing: of the spells those two know that the path offers at all, <b>91 of 91</b> and
		///     <b>100 of 100</b> are feat-taught. What the gate appears to "miss" (18 and 27) is already not offered
		///     today - pets, mounts, emotes and the <c>Undead Minion:</c> / <c>Deathless Acolyte:</c> lines, none of
		///     which are in <c>ClassSpells</c> for the class.
		///     <para>
		///         It also makes every granted spell castable. The client refuses a cast until the character carries
		///         the feat that teaches the spell (<c>YouCanNotUseYet</c>), which is what
		///         <c>LevelUpService.GrantCastabilityFor</c> exists to satisfy; with this gate there is by construction
		///         always such a feat to train.
		///     </para>
		///     <para>
		///         Per class, and not a tuned threshold. The population is bimodal with nothing in between: Barbarian,
		///         Guardian, Conqueror, Assassin and Ranger have <b>exactly 0</b> feat-taught rows, and the next class up
		///         has <b>29</b>. So "does the map grant this class anything at all" is a genuine separation rather than a
		///         cut-off chosen to fit. The five are not a data gap of their own making - 227 of their 259 level-gated
		///         entries are combos, which travel the combo book and are already excluded here, so there is little for a
		///         feat to teach.
		///     </para>
		///     <para>
		///         For those five the gate is <b>off</b> and nothing changes: they keep today's answer, wrong as it is
		///         (the Conqueror's 38 are mostly <c>Colossus of War (1..20)</c> and combo debuffs). Silence is not a
		///         statement - gating on it would ship an empty spellbook, and no capture exists to notice. Open work
		///         tracked in Gitea #852.
		///     </para>
		/// </remarks>
		private static Task<bool> FeatMapCovers(DatabaseContext ctx, ConanClass @class)
		{
			return ctx.ClassSpells.Where(cs => cs.Class == @class)
				.AnyAsync(cs => ctx.FeatSpellGrants.Any(g => g.SpellId == cs.SpellId && g.Feat.TemplateCategory == Feat.AutomaticUnlockCategory));
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

			// Deliberately not realm-scoped, unlike every other read of this table. An item's search category is a
			// property of the catalog, not of a realm's auction house - this only reaches for the listings because the
			// captured rows are where an unimported category is legible. Filtering by realm would narrow the evidence
			// for a global fact and make the answer depend on which realm asked.
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

		/// <summary>
		///     The client's character-customization registry, by resource ref.
		/// </summary>
		/// <remarks>
		///     Every player spawn resolves the character's editor choices through it
		///     (<c>PlayerSpawnScaffoldRows.BuildWeightedEntries</c>), so it sits on the world-entry and broadcast paths -
		///     but it is 1072 rows of static seed content that only the importer ever rewrites, so it is read once and
		///     held for the process rather than queried per spawn. Two callers racing the first read is harmless: they
		///     build the same dictionary and one of them wins.
		/// </remarks>
		public Task<IReadOnlyDictionary<string, MorphTarget>> GetMorphTargetRegistry()
		{
			return _morphTargetRegistry ??= LoadMorphTargetRegistry();
		}

		private async Task<IReadOnlyDictionary<string, MorphTarget>> LoadMorphTargetRegistry()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.MorphTargets.AsNoTracking().ToDictionaryAsync(t => t.Name);
		}

		/// <summary>
		///     Random item templates that can be built into a wire item.
		/// </summary>
		/// <remarks>
		///     Every item has a wire token, harvested or minted (<c>AcgHash.ForWire</c>), so a <c>Hash != 0</c> filter
		///     would only exclude the 232 templates no capture happened to show entering an inventory - a gap in the
		///     harvest, not a property of the item. A name is still required: a nameless row has no identity to render.
		/// </remarks>
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

		/// <summary>
		///     One recipe by the id the client's craft request names - the same id <c>DispatchTradeskillComplete</c>
		///     echoes back. The whole book is 603 rows with two Includes, so the craft path takes this rather than
		///     filtering <c>GetRecipes</c>.
		/// </summary>
		public async Task<Recipe?> GetRecipe(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Recipes.Include(r => r.Items).ThenInclude(i => i.Item).FirstOrDefaultAsync(r => r.Id == id);
		}

		/// <summary>
		///     One summonable mount by the id the client sends in its SummonMount action. The summon Spell is included
		///     because the mount's name lives there - a Mount row is its creature id and quality, and the name is the
		///     spell's (<c>Mount.Id</c> is the spell id).
		/// </summary>
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

		/// <summary>
		///     Every spell sharing a GUI stacking line - the group a line-scoped recast covers, which retail locks one
		///     message per member of (a <c>Contract of Protection</c> cast emits five <c>DispatchLockTemplate</c> bodies,
		///     one per rank of line 12). Ordered by id so the emitted sequence is stable rather than storage-dependent.
		/// </summary>
		public async Task<uint[]> SpellIdsOnLine(uint guiStackingLine)
		{
			if (guiStackingLine == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Spells.AsNoTracking().Where(s => s.GuiStackingLine == guiStackingLine).OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync();
		}

		/// <summary>
		///     Every template a consumable's shared cooldown covers - the items on <c>OnUseLockSpellLine</c> and the
		///     on-use spells they cast.
		/// </summary>
		/// <remarks>
		///     The set comes from the client's own item templates rather than from a line catalog: a
		///     <c>CoreLockSpellLineTemplate</c> names a line (health 80000 / stamina 80100 / mana 80200), and the line's
		///     membership is exactly which items declare it, which is why no line table is needed.
		///     <para>
		///         The spells are included because that is what retail locks. In
		///         <c>NecroL80_..._login_use-consumables</c>, using the firework item 3765539 produces three
		///         <c>DispatchLockTemplate</c> bodies - that item template and both spells it casts (3510737 / 3510738) -
		///         so a consumable's lock covers the thing on the bar and what it triggers.
		///     </para>
		///     <para>Ordered so the emitted sequence is stable rather than storage-dependent.</para>
		/// </remarks>
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

		/// <summary>
		///     Every spell's area applications, read whole - a few thousand rows the importer owns, cached by
		///     <c>SpellAreaCatalog</c> because both consumers (the cast resolve and the world tick) are hot paths.
		/// </summary>
		public async Task<SpellAreaEffect[]> GetSpellAreaEffects()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellAreaEffects.AsNoTracking().OrderBy(a => a.SpellId).ThenBy(a => a.Sequence).ToArrayAsync();
		}

		/// <summary>
		///     The conditions on those links, read whole and by the same caller for the same reason. Ordered by
		///     <c>Pos</c> because the run is a postfix expression - re-ordering it changes what it means.
		/// </summary>
		public async Task<SpellAreaEffectCriterion[]> GetSpellAreaEffectCriteria()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellAreaEffectCriteria.AsNoTracking()
				.OrderBy(c => c.SpellId).ThenBy(c => c.Sequence).ThenBy(c => c.Pos).ToArrayAsync();
		}

		/// <summary>
		///     Every spell-scoped stat modifier a spell carries, read whole - 1,571 rows the importer owns, cached by
		///     <c>SpellModifierService</c> because the lookup sits on the cast-resolve path.
		/// </summary>
		public async Task<SpellSpellStatModifier[]> GetSpellStatModifiers()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellSpellStatModifiers.AsNoTracking().OrderBy(m => m.SpellId).ThenBy(m => m.Sequence).ToArrayAsync();
		}

		/// <summary>
		///     The spell line to member-spells map. A line-scoped modifier covers every rank of the line at once, so
		///     resolving one needs this fan-out; done as a single catalog read cached beside the modifiers rather than a
		///     join per cast.
		/// </summary>
		public async Task<(uint Line, uint SpellId)[]> GetSpellLineMembers()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Spells.AsNoTracking()
				.Where(s => s.GuiStackingLine > 0)
				.Select(s => new ValueTuple<uint, uint>(s.GuiStackingLine, s.Id))
				.ToArrayAsync();
		}

		/// <summary>
		///     The server-acted spell effects (pet summons, triggered procs). Read whole in run order - a few dozen rows,
		///     cached in memory by <c>SpellEffectService</c>, and the lookup sits on the cast-resolve path.
		/// </summary>
		public async Task<SpellEffect[]> GetSpellEffects()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellEffects.OrderBy(e => e.SpellId).ThenBy(e => e.Trigger).ThenBy(e => e.Sequence).ToArrayAsync();
		}

		/// <summary>
		///     Every spell's [2][9] stat effects. Read whole - 1.7k rows over 866 spells, cached in memory by
		///     <c>BuffStatEffects</c>, because a per-spell query on the buff-apply path would hit the DB on every cast.
		/// </summary>
		public async Task<SpellStatEffect[]> GetSpellStatEffects()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.SpellStatEffects.OrderBy(e => e.SpellId).ThenBy(e => e.StatId).ToArrayAsync();
		}

		/// <summary>
		///     The conditions on each effect, keyed the way the effect is, with the run in <c>Pos</c> order - the run is a
		///     postfix expression, so the order is part of the data.
		/// </summary>
		public async Task<ILookup<(uint SpellId, uint Sequence), SpellStatEffectCriterion>> GetSpellStatEffectCriteria()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx.SpellStatEffectCriteria.OrderBy(c => c.SpellId).ThenBy(c => c.Sequence).ThenBy(c => c.Pos).ToArrayAsync();
			return rows.ToLookup(c => (c.SpellId, c.Sequence));
		}

		/// <summary>
		///     The spells a newly created or newly logged-in character's action bar is seeded with.
		/// </summary>
		/// <remarks>
		///     The bar seeds from the book - <see cref="GetStartingSpells" /> verbatim - rather than re-deriving a
		///     candidate set of its own. A second copy of the rules drifts: level 1 is the create kit's exclusive domain
		///     on the book side (a captured list, not a level query), so an ordinary level query here seeds a level-4
		///     Necromancer's bar with <c>Pestilential Blast (Rank 1)</c> - Level 1, in the class catalog, and not a spell
		///     the character has been taught. A slot pointing outside the book is a dead button. Caught by
		///     <c>--report spell-grant-check</c>.
		/// </remarks>
		public async Task<uint[]> GetStartingBarSpells(uint level, ConanClass @class)
		{
			var book = await GetStartingSpells(level, @class);

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var candidates = await ctx.Spells
				.Where(s => book.Contains(s.Id))
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

		/// <summary>
		///     The spells a character of this class/level should already know: the create-granted starter kit, plus
		///     (above level 1) everything its class has access to whose level requirement it meets.
		/// </summary>
		/// <remarks>
		///     Level 1 is the kit's exclusive domain, and that is capture-proven: a create capture shows retail granting
		///     the Necromancer exactly <c>Ice Strike (Rank 1)</c> + <c>Frozen Hatred</c>. A plain "Level &lt;= 1" query
		///     returns six - the class also has access to four other spells the RDB rates level 1 (Vile Insight,
		///     Pestilential Blast R1, ...) that retail demonstrably does not grant at create. So the create kit is a
		///     fixed list, not "every level-1 spell", and the level query starts at 2.
		///     <para>
		///         Conversely the kit ignores the requirement entirely - retail grants <c>Ice Strike (Rank 1)</c> at
		///         creation though the RDB rates it level 7 (Hellfire Breath R1: level 25). Level never reaches the
		///         client (DispatchLearnSpell carries the spell id alone), so it is purely this gate; the client's own
		///         view comes from its RDB. Pinning the kit's spells to Level 1 to express this would overwrite real
		///         levels with sentinels - see <see cref="ClassSpell.IsStarter" />.
		///     </para>
		///     <para>
		///         Above level 1 the gate prefers <see cref="ClassSpell.GrantLevel" /> - the level a capture shows this
		///         class being granted this spell - and falls back to <c>Spells.Level</c> only where no capture has
		///         caught it. The two are different facts, not rival measurements: retail grants at or below the RDB
		///         field, never above (Desecrating Essence R1 granted at 3 vs field 5; Contract of Protection R1 at 5 vs
		///         10), so the field is a trainer / full-power level and the capture is the unlock. Only a handful of
		///         GrantLevels exist so far; every gameplay capture containing a ding adds more, and unlike a scrape they
		///         cannot be wrong about what retail granted.
		///     </para>
		/// </remarks>
		public async Task<uint[]> GetStartingSpells(uint level, ConanClass @class)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var featGated = await FeatMapCovers(ctx, @class);
			var increments = await StackingIncrements(ctx); // as in GetClassSpellsUnlockedBetween
			return await ctx
				.ClassSpells.Where(cs => cs.Class == 0 || cs.Class == @class)
				.Join(ctx.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => new
				{
					cs,
					s
				})
				// The level branch excludes combos and effects, and (for a class the feat map speaks for) keeps only
				// what a feat teaches - none of the three is a spellbook spell the level path may grant, see
				// GetClassSpellsUnlockedBetween and FeatMapCovers. The starter kit is left alone by all three: it is a
				// captured list, so whatever it holds is what retail granted (and in fact holds no combo GameAction, and
				// every one of its members is feat-taught anyway).
				.Where(x => x.cs.IsStarter || (level > 1 && !ctx.Combos.Any(c => c.GameActionId == x.s.Id)
					&& !ctx.Spells.Any(applier => applier.EffectSpell == x.s.Id && applier.Level > 0 && ctx.ClassSpells.Any(cs => cs.SpellId == applier.Id))
					&& !ctx.SpellAreaEffects.Any(chain => chain.EffectSpellId == x.s.Id && ctx.Combos.Any(c => c.GameActionId == chain.SpellId))
					&& !increments.Contains(x.s.Id)
					&& (!featGated || ctx.FeatSpellGrants.Any(g => g.SpellId == x.s.Id && g.Feat.TemplateCategory == Feat.AutomaticUnlockCategory))
					&& (x.cs.GrantLevel != null
					? x.cs.GrantLevel <= level
					: x.s.Level > 1 && x.s.Level <= level)))
				.OrderBy(x => x.s.Level)
				.ThenBy(x => x.s.Id)
				.Select(x => x.s.Id)
				.Distinct()
				.ToArrayAsync();
		}

		// Returns the rows, not bytes: the 0x205D payload is rebuilt from their columns by RemoteFormatXml
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
