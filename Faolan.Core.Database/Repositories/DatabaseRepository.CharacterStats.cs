using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	// What the character adds up to: the stat sheet and the inputs it is computed from - the class base, the buffs,
	// and what the worn gear, the trained feats and the combos contribute. One of the five character slices; the
	// feats and spells themselves are ICharacterProgressionRepository.
	public interface ICharacterStatRepository
	{
		Task<CharacterBuff[]> GetCharacterBuffs(uint characterId);
		Task<FeatSpellStatModifier[]> GetCharacterFeatSpellStatModifiers(uint characterId);
		Task<FeatEffect[]> GetCharacterFeatEffects(uint characterId);

		/// <summary>
		///     The stat effects of the feats the client trains by itself for this class at or below this level - the
		///     second, derived half of a character's feat contribution.
		/// </summary>
		/// <remarks>
		///     These feats are deliberately absent from <c>CharacterFeats</c>: retail never puts one on the wire and
		///     <c>FeatHolder::TrainFeat</c> refuses one outright, so the character has no list to read them off. The
		///     client derives them from (class, level) against its featclass table and so does this. No wire change -
		///     server-side stat contributions only.
		///     <para>
		///         It is a ladder pick, not a sum, and the class progression track is excluded because
		///         <see cref="GetClassAttributeBase" /> already applies it - see <see cref="AutoTrainedFeatLadder" />
		///         for both, and for the three captured regen observations that corroborate the rule.
		///     </para>
		/// </remarks>
		Task<FeatEffect[]> GetClassAutoTrainedFeatEffects(ConanClass characterClass, uint level);

		/// <summary>
		///     The per-spell boosts a character's trained feats grant, as <c>spellId -> percent x 100</c>. Resolved
		///     through the spell line the feat actually names, so every rank of a boosted spell is in the result and
		///     combat needs no line lookup of its own. Summed per spell, because two different feats can boost the same
		///     line; a character holds only the highest rank of each feat, so the ranks of one feat never stack with
		///     each other.
		/// </summary>
		Task<IReadOnlyDictionary<uint, int>> GetCharacterSpellBoosts(uint characterId);

		/// <summary>
		///     What a character's trained feats do to their combos, as <c>comboId -> bonus</c>. Summed per combo across
		///     every feat that names it, on the same "a character holds only the highest rank of each feat" rule
		///     <see cref="GetCharacterSpellBoosts" /> relies on.
		/// </summary>
		Task<IReadOnlyDictionary<uint, ComboFeatBonus>> GetCharacterComboBonuses(uint characterId);

		/// <summary>
		///     The procs a character's trained feats hang off its combos and spell lines - every
		///     <see cref="FeatProc" /> row of every held feat, resolved through the wire-to-template feat map. Empty for
		///     a character with no proc feats, which is most of them.
		/// </summary>
		Task<IReadOnlyList<FeatProc>> GetCharacterFeatProcs(uint characterId);

		/// <summary>
		///     The per-spell scaling-attribute overrides among a character's known spells, as
		///     <c>spellId -> wire stat id</c>. Only spells whose template carries RDB field 847 appear; every other
		///     spell takes the caster's class attribute (<c>CombatResolver.MagicDamageAttribute</c>), so an absent
		///     entry means "no override", not "no scaling".
		/// </summary>
		Task<IReadOnlyDictionary<uint, uint>> GetCharacterSpellScalingAttributes(uint characterId);
		Task<CharacterStat[]> GetCharacterStats(uint characterId);
		Task<Item[]> GetEquippedStatItems(uint characterId, WeaponSet activeSet);

		// The class/level base stats: what a character of this class and level starts with before gear and buffs.
		// Empty when that (class, level) has no decoded row - all 12 classes x 80 levels are shipped, so this means a
		// level outside 1..80 or an unimported database - and the caller must treat empty as unknown, not as zero.
		Task<IReadOnlyDictionary<CharacterStatId, int>> GetClassAttributeBase(ConanClass characterClass, uint level);
		Task<(int Min, int Max)?> GetEquippedWeaponDamage(uint characterId);
		Task SaveCharacterBuffs(uint characterId, IReadOnlyCollection<CharacterBuff> buffs);

		/// <summary>
		///     Sets the given stat sheet slots to new values (the skillpoint spend). A stat id may occupy more than one
		///     ordinal (StatList#1 mirrors displayed stats that StatList#2 also carries), so every row holding the id is
		///     written - the sheet must not disagree with itself. Ids with no row are ignored: the sheet is what the
		///     snapshot is authored from, and inventing an ordinal would put a stat on the wire in a position no capture
		///     has.
		/// </summary>
		Task SaveCharacterStats(uint characterId, IReadOnlyDictionary<CharacterStatId, uint> values);

		/// <summary>
		///     The same write, but minting a sheet slot for an id the character has no row for (the daily challenge
		///     counters). Deliberately a separate method rather than a flag on <see cref="SaveCharacterStats" />: that
		///     one's refusal to invent an ordinal is a rule the skillpoint spend depends on, and this is the one case
		///     where minting is right - retail's own login snapshots carry stats 1000066..1000077 (46-72 of the 134
		///     captures), so a character that has never had one is missing a row retail would have sent. A new row is
		///     appended after the character's highest ordinal.
		/// </summary>
		Task UpsertCharacterStats(uint characterId, IReadOnlyDictionary<CharacterStatId, uint> values);
	}

	public partial class DatabaseRepository
	{
		public async Task<CharacterBuff[]> GetCharacterBuffs(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterBuffs.Where(b => b.CharacterId == characterId).OrderBy(b => b.BuffId).ToArrayAsync();
		}

		// The spell-scoped stat modifiers the character's trained feats carry - "your Shock costs 10% less mana",
		// "every rank of your Frost line crits harder". The feat-sourced half of the one mechanism; the other half
		// rides live buffs and is read straight off the catalog (SpellSpellStatModifier).
		//
		// Two hops, for the same reason GetCharacterFeatEffects has two: CharacterFeats holds the wire feat id and the
		// modifier rows are keyed by the RDB template id, which FeatIdMaps bridges. The line -> rank fan-out is not
		// done here - the rows keep the target space the client stated, and SpellModifierService resolves it once
		// against the catalog, so a line row does not become N rows on every query.
		public async Task<FeatSpellStatModifier[]> GetCharacterFeatSpellStatModifiers(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
						  join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
						  join mod in ctx.FeatSpellStatModifiers on map.TemplateId equals mod.FeatId
						  select mod).ToArrayAsync();
		}

		// The stat modifiers a character's trained feats grant. Two hops, because the wire and the catalog use
		// different id spaces: CharacterFeat.FeatId is a wire id, FeatIdMap resolves it to the RDB template id,
		// and FeatEffect hangs off the template.
		//
		// A feat whose template is one of the TEA-encrypted ones contributes nothing - we cannot read its effects -
		// so this returns fewer rows than the character has feats, which is expected rather than a fault.
		public async Task<FeatEffect[]> GetCharacterFeatEffects(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
						  join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
						  join effect in ctx.FeatEffects on map.TemplateId equals effect.FeatId
						  select effect).ToArrayAsync();
		}

		// The auto-trained half. Same two id-space hops as the trained path above, but the left side is the client's
		// featclass schedule rather than a character's list, because these feats are never in one.
		//
		// Two filters carry the whole correctness of this, and both are argued in AutoTrainedFeatLadder: the class
		// progression track is excluded (ClassAttributeBases already applies it, from the same client templates), and
		// what survives is ladder-picked per stat rather than summed.
		public async Task<FeatEffect[]> GetClassAutoTrainedFeatEffects(ConanClass characterClass, uint level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var reached = await (from entry in ctx.FeatClassEntries.AsNoTracking()
					.Where(e => e.Autotrain && e.Class == characterClass && e.Level <= level)
								 join map in ctx.FeatIdMaps on entry.FeatId equals map.WireFeatId
								 join feat in ctx.Feats on map.TemplateId equals feat.Id
								 join effect in ctx.FeatEffects on map.TemplateId equals effect.FeatId
								 where feat.Name == null || !feat.Name.Contains(FeatRules.ClassProgressionTrackMarker)
								 select new AutoTrainedFeatLadder.Rung(entry.Level, entry.Id, effect)).ToArrayAsync();

			return AutoTrainedFeatLadder.Select(reached);
		}

		public async Task<IReadOnlyDictionary<uint, int>> GetCharacterSpellBoosts(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Two hops. CharacterFeats holds the wire feat id and the modifier rows are keyed by the template id (a
			// different id space, hence FeatIdMaps); then the modifier names a spell line, which fans out to every
			// rank of that spell. Resolving the fan-out here - once, at world entry - is what keeps the cast path a
			// dictionary lookup instead of a per-cast query.
			var rows = await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
							  join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
							  join mod in ctx.FeatSpellModifiers on map.TemplateId equals mod.FeatId
							  join spell in ctx.Spells on mod.SpellLineId equals spell.GuiStackingLine
							  select new { spell.Id, mod.PercentHundredths }).ToArrayAsync();

			var totals = new Dictionary<uint, int>();
			foreach (var row in rows)
				totals[row.Id] = totals.GetValueOrDefault(row.Id) + row.PercentHundredths;

			return totals;
		}

		// What a character's trained feats do to their combos. Two hops for the same reason GetCharacterSpellBoosts has
		// two: CharacterFeat.FeatId is a wire id and the modifier rows hang off the RDB template id, which FeatIdMaps
		// bridges.
		//
		// Two tables, kept apart in the result because they are not the same statement: 53250 modifies one weapon
		// attack of the combo (its opener or its finisher) and 53209 modifies the combo as a whole. Resolved here,
		// once at world entry, so the swing path stays a dictionary lookup.
		//
		// The two tables' units differ - 53250's are whole percents and 53209's hundredths - which is the client's own
		// doing. Normalised to hundredths here so nothing downstream has to remember which is which.
		public async Task<IReadOnlyList<FeatProc>> GetCharacterFeatProcs(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
						  join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
						  join proc in ctx.FeatProcs on map.TemplateId equals proc.FeatId
						  orderby proc.FeatId, proc.Sequence
						  select proc).ToListAsync();
		}

		public async Task<IReadOnlyDictionary<uint, ComboFeatBonus>> GetCharacterComboBonuses(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var attacks = await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
								 join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
								 join mod in ctx.FeatComboAttackModifiers on map.TemplateId equals mod.FeatId
								 select new { mod.ComboId, mod.AttackSlot, mod.DpsPercent, mod.CriticalChanceRating }).ToArrayAsync();

			var upgrades = await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
								  join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
								  join mod in ctx.FeatComboModifiers on map.TemplateId equals mod.FeatId
								  select new { mod.ComboId, mod.DamagePercentHundredths }).ToArrayAsync();

			var totals = new Dictionary<uint, ComboFeatBonus>();
			foreach (var row in attacks)
			{
				var bonus = totals.GetValueOrDefault(row.ComboId, ComboFeatBonus.None);
				totals[row.ComboId] = row.AttackSlot == ComboAttackSlot.Opening
					? bonus with
					{
						OpeningDamagePercentHundredths = bonus.OpeningDamagePercentHundredths + (row.DpsPercent * 100),
						OpeningCriticalRating = bonus.OpeningCriticalRating + row.CriticalChanceRating
					}
					: bonus with
					{
						FinishingDamagePercentHundredths = bonus.FinishingDamagePercentHundredths + (row.DpsPercent * 100),
						FinishingCriticalRating = bonus.FinishingCriticalRating + row.CriticalChanceRating
					};
			}

			foreach (var row in upgrades)
			{
				var bonus = totals.GetValueOrDefault(row.ComboId, ComboFeatBonus.None);
				totals[row.ComboId] = bonus with { SpellDamagePercentHundredths = bonus.SpellDamagePercentHundredths + row.DamagePercentHundredths };
			}

			return totals;
		}

		public async Task<IReadOnlyDictionary<uint, uint>> GetCharacterSpellScalingAttributes(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Only the character's own spells, and only the ones that actually carry an override - 716 of the 21,502
			// templates do, so a character typically has a handful or none, and the cast path stays a dictionary
			// lookup (the same reason GetCharacterSpellBoosts resolves its fan-out here).
			var rows = await (from known in ctx.CharacterSpells.Where(c => c.CharacterId == characterId)
							  join spell in ctx.Spells on known.SpellId equals spell.Id
							  where spell.ScalingAttribute != 0
							  select new { spell.Id, spell.ScalingAttribute }).ToArrayAsync();

			return rows.ToDictionary(r => r.Id, r => r.ScalingAttribute);
		}

		public async Task<CharacterStat[]> GetCharacterStats(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterStats.Where(s => s.CharacterId == characterId).OrderBy(s => s.Ordinal).ToArrayAsync();
		}

		// The class/level base stats. Empty when that (class, level) has no decoded row - some classes' higher levels
		// are not located yet - and the caller must treat empty as unknown rather than as zero.
		public async Task<IReadOnlyDictionary<CharacterStatId, int>> GetClassAttributeBase(ConanClass characterClass, uint level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassAttributeBases.AsNoTracking()
				.Where(r => r.Class == characterClass && r.Level == level)
				.ToDictionaryAsync(r => r.StatId, r => r.Amount);
		}

		// The character's worn stat-bearing items (the 16 combat slots) with their StatModifiers loaded, one entry per
		// occupied slot (two identical rings -> the item twice), for server-side gear-stat aggregation.
		//
		// Only the active weapon set's two slots are folded in - armour + jewellery always apply. A character can have
		// both loadouts equipped at once (Primary = slots 6/8, Secondary = 20/21) and counting all four would
		// double-count. The two sets are swappable loadouts, not inherently melee vs ranged.
		//
		// The result is one entry per occupied slot, and the distinct set is for the query only - `IN (...)` cannot use
		// a duplicate, and loading the same template twice is just a second copy of one row. Returning the distinct set
		// would silently halve every stat the character wears twice: their pools, their attribute scores, and the
		// attribute-scaled part of every pool with them.
		public async Task<Item[]> GetEquippedStatItems(uint characterId, WeaponSet activeSet)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var wielded = activeSet == WeaponSet.Primary
				? new[] { CharacterEquipmentSlot.MainWeapon, CharacterEquipmentSlot.OffHandWeapon }
				: [CharacterEquipmentSlot.RangedWeapon, CharacterEquipmentSlot.Ammo];
			// The stat-bearing set minus the weapon slots of the loadout that is not drawn. Vanity slots are appearance
			// only and the Bag / ResourceBag containers carry a bag stat, not a combat one - neither is stat-bearing.
			var slots = CharacterEquipmentSlot.StatBearing
				.Where(slot => !IsWeaponSlot(slot))
				.Concat(wielded)
				.Select(slot => (int)slot)
				.ToArray();

			var wornInstances = await ctx.ItemInstances.AsNoTracking()
				.Where(i => i.OwnerCharacterId == characterId && i.Container == ContainerKind.Equipped && slots.Contains(i.Slot))
				.Select(i => new
				{
					i.Id,
					i.TemplateId
				})
				.ToArrayAsync();
			if (wornInstances.Length == 0)
				return [];

			// The gems socketed into those worn items count too, and they count through the ordinary item-stat path - a
			// socket carries no stat value in either wire form, so a gem's effect is its own template's modifier list.
			// Returning them as extra entries is the whole of it, because the caller sums StatModifiers across the array.
			//
			// One entry per socket, exactly as the worn set is one entry per occupied slot: two of the same gem in two
			// sockets is that bonus twice. Distinct-ing here would halve it.
			var wornIds = wornInstances.Select(i => i.Id).ToArray();
			var gems = await ctx.ItemInstanceSockets.AsNoTracking()
				.Where(s => wornIds.Contains(s.ItemInstanceId))
				.Select(s => s.GemItemId)
				.ToArrayAsync();

			var templates = wornInstances.Select(i => i.TemplateId).Concat(gems).ToArray();
			var ids = templates.Distinct().ToArray();
			var byId = await ctx.Items.AsNoTracking().Include(i => i.StatModifiers).Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
			return templates.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();

			static bool IsWeaponSlot(CharacterEquipmentSlot slot)
			{
				return slot is CharacterEquipmentSlot.MainWeapon or CharacterEquipmentSlot.OffHandWeapon or CharacterEquipmentSlot.RangedWeapon or CharacterEquipmentSlot.Ammo;
			}
		}

		// The (min, max) damage of the character's equipped melee main-hand weapon (MainWeapon), for melee resolution,
		// or null if that slot is empty or the item carries no imported weapon damage. A lightweight projection - no
		// nav graph loaded (the combat swing hot path).
		public async Task<(int Min, int Max)?> GetEquippedWeaponDamage(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			const int mainWeapon = (int)CharacterEquipmentSlot.MainWeapon;
			var dmg = await ctx.ItemInstances
				.Where(i => i.OwnerCharacterId == characterId && i.Container == ContainerKind.Equipped && i.Slot == mainWeapon)
				.Join(ctx.Items, i => i.TemplateId, i => i.Id, (_, i) => new
				{
					i.MinDamage,
					i.MaxDamage
				})
				.FirstOrDefaultAsync();

			return dmg is { MinDamage: { } min, MaxDamage: { } max }
				? (min, max)
				: null;
		}

		public async Task SaveCharacterBuffs(uint characterId, IReadOnlyCollection<CharacterBuff> buffs)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			ctx.CharacterBuffs.RemoveRange(ctx.CharacterBuffs.Where(b => b.CharacterId == characterId));
			foreach (var buff in buffs)
			{
				buff.CharacterId = characterId;
				buff.Character = null!;
				ctx.CharacterBuffs.Add(buff);
			}

			await ctx.SaveChangesAsync();
		}

		public async Task SaveCharacterStats(uint characterId, IReadOnlyDictionary<CharacterStatId, uint> values)
		{
			if (values.Count == 0)
				return;

			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var ids = values.Keys.ToArray();
			var rows = await ctx.CharacterStats.Where(s => s.CharacterId == characterId && ids.Contains(s.StatId)).ToArrayAsync();
			foreach (var row in rows)
				row.Value = values[row.StatId];

			await ctx.SaveChangesAsync();
		}

		public async Task UpsertCharacterStats(uint characterId, IReadOnlyDictionary<CharacterStatId, uint> values)
		{
			if (values.Count == 0)
				return;

			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var ids = values.Keys.ToArray();
			var rows = await ctx.CharacterStats.Where(s => s.CharacterId == characterId && ids.Contains(s.StatId)).ToArrayAsync();
			foreach (var row in rows)
				row.Value = values[row.StatId];

			var missing = values.Keys.Except(rows.Select(r => r.StatId)).ToArray();
			if (missing.Length != 0)
			{
				// Append after the character's highest ordinal - the sheet is ordered by it, so an appended stat lands
				// at the end of the snapshot's stat block rather than displacing a captured one.
				var nextOrdinal = await ctx.CharacterStats.Where(s => s.CharacterId == characterId).Select(s => (int?)s.Ordinal).MaxAsync() ?? -1;
				foreach (var statId in missing)
					ctx.CharacterStats.Add(new CharacterStat { CharacterId = characterId, Ordinal = ++nextOrdinal, StatId = statId, Value = values[statId] });
			}

			await ctx.SaveChangesAsync();
		}
	}
}
