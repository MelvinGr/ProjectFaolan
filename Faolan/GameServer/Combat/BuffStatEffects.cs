using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The buff STAT engine (#367): what a live buff does to the entity carrying it, beyond rendering an icon.
	//
	// A spell's stat deltas live in its RDB template's [2][9] region (SpellStatEffect - imported per spell as
	// (kind, stat, amount)). This turns the ones we can JUSTIFY into live entity stats: on every change to an entity's
	// ActiveBuffs, the affected stats are recomputed as `gear + sum(active buffs)` and the changed ones pushed to the
	// client. Recomputed, never incremented: an add/subtract pair drifts the moment a buff is refreshed, re-applied on
	// relog, or cleared by death, and a drifted armor rating is invisible until combat feels wrong.
	//
	// WHAT IT HONORS, and why only that:
	//   - the record's KIND must mean "flat" (FlatKinds). The same region also carries PERCENT records (kind 53067:
	//     Philtre of Guile 0x138 = 200 is "+2% Critical Rating") and health effects (kind 53219: an instant heal, or
	//     the -100000000 drain sentinel). Reading a percent record as flat turns +2% into +200, so an unknown kind is
	//     never honored.
	//   - the STAT must be one whose wire scale we verified against a real tooltip - which, not coincidentally, is
	//     exactly the set the server already consumes from GEAR (MaxStatAggregator + WorldEntity's rating fields,
	//     #3/#399/#401) plus the five attributes. A buff and a ring granting "+62 Hit Rating" are the same value in
	//     the same units, so the value already proven right for the ring is proven right for the buff.
	// Everything else stays imported-but-inert and is counted in the load summary, so the gap is visible rather than
	// silently applied with a guessed scale. docs/rdb/spell-stat-effects.md has the kind classification and its
	// evidence; the percent kinds need a modifier model (they scale a TOTAL, which the server does not compute yet).
	//
	// ⚠️ NOT a general spell-effect dispatcher. Pet summons, mounts, the Shield of the Risen, spellweave trances and the
	// fatality proc are their own paths (SpellEffectService for the data-driven ones, MountCatalog / SpellweaveCatalog
	// for the mount- and class-keyed ones). This owns one question - "what does an active buff add to its carrier's
	// stats" - and answers it from data.
	public sealed class BuffStatEffects
	{
		// The PERCENT kind: the Amount is a percentage x100, so 200 is "+2%" - confirmed against the client's own elixir
		// descriptions, whose 2%/5% tiers carry 200/500 and 10%/20% carry 1000/2000. It scales a computed TOTAL rather
		// than adding to it, which is why it needed a model of its own (#436).
		internal const uint PercentKind = 53067;

		// A percentage's denominator: 10000 == +100%. Retail uses the same x10000 scale for the multiplier STATS it
		// keeps a percentage in - a live read of the fatality buff shows stat 536 "Magic Damage Modifier" moving
		// 10000 -> 12000 for a +20% effect, i.e. 10000 IS 1.00.
		private const int PercentScale = 10000;

		// The kinds whose Amount ADDS to the stat (as opposed to a percent modifier or a health effect). Classified
		// empirically over all 1714 imported records: these carry values that read as flat in their own units against
		// the spell's rank ladder and the client's description text (Fend +1000 Armor / +500 Protection, Battle Cry
		// +62 Hit Rating, "Augmente la Dextérité de 1" for the 0x32E=1 record).
		//
		// ⚠️ 53220/53221 are additive but NOT flat: they are a LEVEL RAMP whose Amount is only the low endpoint, and
		// the row's three ramp columns carry the rest (#599). Honored through LevelRamp at the carrier's level - see
		// Delta.At. Their percent twin 53068 `InterpolatedModifyAsRating` is deliberately absent, for the same reason
		// 53067 is: a percentage is not an addition.
		private static readonly HashSet<uint> FlatKinds = [53045, 53189, 53220, 53221];

		// The stats a flat record is applied for: where the live value lands, and what the client is TOLD. The apply is
		// null for the ones the server does not model yet, which are still pushed so the character sheet reflects the
		// buff; when it returns a value, that value is pushed instead of the buffed total (the max pools, where the
		// wire wants the whole pool, not the bonus).
		//
		// ⚠️ THE ADMISSION TEST IS NOT "IS THE SCALE VERIFIED" ANY MORE, and reading it that way is what let entries
		// rot on the wrong side of this list. That was the test when a scale could only come from a hand-read tooltip;
		// `StatDisplayScale` now transcribes the CLIENT'S OWN divisor rule and covers every id, so the scale of every
		// stat in the catalog is known and that gate admits everything.
		//
		// What still keeps a stat out is the SECOND, unstated criterion, and it is the load-bearing one:
		// `Recompute` computes `gear + sum(buffs)`, so a stat is only admissible if its baseline is ZERO. A
		// MULTIPLIER stat is not: `MovementMultiplier` (0x0F0) has a baseline of 100 and `Turtle Formation` carries
		// −50 on it, so summing from zero would tell the client "movement 50%" for what the buff means as "−50%"
		// - and `MagicDamageModifier` (0x218) is worse, with a baseline of 10000 that a live read shows moving to
		// 12000 for +20%. Those need a model that knows the baseline (#700), not a place on this list.
		//
		// So: add a stat here when its records ADD to zero, whatever their scale. Leave it off when they scale a
		// total.
		private static readonly Dictionary<CharacterStatId, Func<WorldEntity, uint, uint>?> HonoredStats = new()
		{
			// Gear-consumed ratings (#3/#399/#401) - scales proven against in-client item tooltips.
			[CharacterStatId.Armor] = (e, v) => e.ArmorRating = v,
			[CharacterStatId.Protection] = (e, v) => e.Protection = v,
			[CharacterStatId.HitRating] = (e, v) => e.HitRating = v,
			[CharacterStatId.CriticalRating] = (e, v) => e.CriticalRating = v,
			[CharacterStatId.CriticalDamageRating] = (e, v) => e.CriticalDamageRating = v,
			[CharacterStatId.CombatRating] = (e, v) => e.CombatRating = v,
			[CharacterStatId.MagicDamage] = (e, v) => e.MagicDamage = v,
			[CharacterStatId.NaturalManaRegen] = (e, v) => e.NaturalManaRegen = v,
			[CharacterStatId.NaturalStaminaRegen] = (e, v) => e.NaturalStaminaRegen = v,

			// The three MAX POOLS (#450). A buff really does raise them - Focused Mind carries stat 507 across its whole
			// rank ladder, Sustaining Wrath stat 1 in 9000-wire steps - and the values are stored at the ordinary x100
			// vitals scale, which the rank ladder forces (Sustaining Wrath's 108000..180000 is +1080..+1800 health; read
			// flat it would be +108000). Until this they were imported and inert, so a buffed player's bar stopped at
			// their unbuffed max and out-of-combat regen visibly stalled there.
			//
			// These go through EntityVitals.SetMaxFromBonus rather than a field write because the pool is
			// `base + gear + buffs` and current has to be clamped when a buff ENDS and the max comes back down.
			[CharacterStatId.HealthMax] = (e, v) => e.Vitals?.SetMaxFromBonus(CharacterStatId.HealthMax, (int)v) ?? v,
			[CharacterStatId.ManaMax] = (e, v) => e.Vitals?.SetMaxFromBonus(CharacterStatId.ManaMax, (int)v) ?? v,
			[CharacterStatId.StaminaMax] = (e, v) => e.Vitals?.SetMaxFromBonus(CharacterStatId.StaminaMax, (int)v) ?? v,

			// The three RESISTANCE stats a buff can move. All three are ×1 (docs/subsystems/player-stats.md), so there
			// is no scale to verify - which is the only thing this gate is for - and between them they were dropping
			// 370 imported rows on the floor: `Turtle Formation` +100 knockback resistance, `Phoenix Cloak` +40/+50
			// damage deflection, and the `-20 Chance of Being Staggered` every horse's `Mounted` buff carries.
			//
			// KnockbackResistance is the one with a live consumer: `KnockbackService` already reads it off
			// `BuffedStats` and falls back to the gear bonus, so honoring it here is what makes a knockback-resisting
			// BUFF actually resist one rather than only a knockback-resisting item.
			//
			// The other two are sheet-only for now, like the attributes below: the stat is real and the client draws
			// it, but no server model consults it - there is no stagger mechanic at all (#699), and damage deflection
			// has no consumer either.
			[CharacterStatId.KnockbackResistance] = null,
			[CharacterStatId.DamageDeflection] = null,
			[CharacterStatId.StaggeringChance] = null,

			// The DAMAGE-SCHOOL invulnerability ratings, all eleven (#699). Additive from a zero baseline, at the
			// client's ×100: `Magic Shield` +10000 is +100% cold, `Crom` +20000, `Fire Pit` −20000. Nothing consumes
			// them yet - the mitigation model is per-armour/protection, not per school - but they are the largest
			// single family the gate was dropping (~700 rows) and the sheet should show them.
			[CharacterStatId.ColdInvulnerability] = null,
			[CharacterStatId.SlashingInvulnerability] = null,
			[CharacterStatId.PiercingInvulnerability] = null,
			[CharacterStatId.CrushingInvulnerability] = null,
			[CharacterStatId.FireInvulnerability] = null,
			[CharacterStatId.ElectricalInvulnerability] = null,
			[CharacterStatId.HolyInvulnerability] = null,
			[CharacterStatId.UnholyInvulnerability] = null,
			[CharacterStatId.PoisonInvulnerability] = null,
			[CharacterStatId.SiegeInvulnerabilityRating] = null,
			[CharacterStatId.GenericInvulnerabilityRating] = null,

			// The OUT-OF-COMBAT regen trio, and the natural-health regen whose two siblings (mana and stamina) were
			// already honored above - a plain oversight, since 0x383/0x384/0x385 are one run and only the middle two
			// were listed. `Hearty Meal` carries a 264…2640 rank ladder on the health one.
			[CharacterStatId.NaturalHealthRegen] = null,
			[CharacterStatId.NonCombatHealthRegen] = null,
			[CharacterStatId.NonCombatManaRegen] = null,
			[CharacterStatId.NonCombatStaminaRegen] = null,

			// Additive ratings with no consumer yet: `Heightened Sense` +2500 evade (+25%), `Broken Bones` −5000.
			[CharacterStatId.EvadeChance] = null,
			[CharacterStatId.BaseSpellDamage] = null,
			[CharacterStatId.HealthTapAmount] = null,

			// The five attributes: display-unit amounts (see SpellScale), pushed for the sheet. No server model reads
			// them yet - the attribute-driven formulas (armor from Strength, …) are still client-side.
			[CharacterStatId.Strength] = null,
			[CharacterStatId.Constitution] = null,
			[CharacterStatId.Intelligence] = null,
			[CharacterStatId.Wisdom] = null,
			[CharacterStatId.Dexterity] = null
		};

		private readonly IDatabaseRepository _database;
		private readonly SemaphoreSlim _loadLock = new(1, 1);
		private readonly ILogger _logger;

		// The honored flat + percent deltas per spell BASE id. Loaded once (1.7k rows) on first use.
		private Catalog? _catalog;

		public BuffStatEffects(IDatabaseRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// The honored deltas a buff carries, in display units, resolved for a carrier of the given level (a level ramp
		// is worth more the higher the carrier - #599). Empty for a spell with no region, or one whose records are all
		// percent / health / unmodelled-stat.
		public async Task<IReadOnlyList<(CharacterStatId Stat, int Amount)>> ForSpellBase(uint spellBase, uint level)
		{
			var catalog = await EnsureLoaded();
			return catalog.BySpellBase.TryGetValue(spellBase, out var deltas)
				? [.. deltas.Select(d => (d.Stat, d.At(level)))]
				: [];
		}

		// Recomputes `gear + active buffs` for every honored stat on this entity, writes the live fields, and returns
		// the stats whose value CHANGED with their new display value (so the caller can push exactly those). Empty when
		// nothing moved - the common case, since most buffs carry no honored deltas.
		public async Task<IReadOnlyList<(CharacterStatId Stat, uint Value)>> Sync(WorldEntity entity)
		{
			return Recompute(entity, await EnsureLoaded());
		}

		// The catalog build, as a pure function over the imported rows: filter to what we honor and convert each
		// amount to display units. Also reports what was dropped, so the load summary can say how big the gap is.
		internal static Catalog BuildCatalog(IEnumerable<SpellStatEffect> rows)
		{
			var honored = new Dictionary<uint, List<Delta>>();
			var percent = new Dictionary<uint, List<Delta>>();
			int percentSkipped = 0, kindSkipped = 0, statSkipped = 0;
			foreach (var row in rows)
			{
				var isPercent = row.EffectKind == PercentKind;
				if (!isPercent && !FlatKinds.Contains(row.EffectKind))
				{
					kindSkipped++;
					continue;
				}

				// The stat gate is the same for both kinds: a percentage of a stat whose scale we have not verified is
				// no more usable than a flat amount of it.
				if (!HonoredStats.ContainsKey(row.StatId))
				{
					if (isPercent)
						percentSkipped++;
					else
						statSkipped++;
					continue;
				}

				// A percentage is NOT in the stat's units - it is x100 of a percent - so it is carried raw, while a
				// flat amount converts to display units here.
				//
				// ⚠️ A RAMPED row carries BOTH endpoints raw and converts AFTER the interpolation instead (Delta.At),
				// because the lerp is the client's float arithmetic over the raw endpoints: dividing them first
				// rounds twice, and the two disagree by one over most of a ramp's span.
				var ramp = !isPercent && row is { HighAmount: { } high, LowLevel: { } low, HighLevel: { } top }
					? new LevelRamp(row.Amount, high, low, top)
					: (LevelRamp?)null;
				var delta = new Delta(row.StatId, isPercent || ramp is not null
					? row.Amount
					: row.Amount / (int)SpellScale(row.StatId), ramp);

				var into = isPercent ? percent : honored;
				(into.TryGetValue(row.SpellId, out var list) ? list : into[row.SpellId] = []).Add(delta);
			}

			return new Catalog(honored.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
				percent.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), percentSkipped, kindSkipped, statSkipped);
		}

		// The recompute, as a pure function over an entity + a catalog: `(gear + flat buffs) x (1 + percent buffs)` per
		// honored stat, written to the live fields, returning only what changed. Separate from Sync so it is testable
		// without a database.
		internal static IReadOnlyList<(CharacterStatId Stat, uint Value)> Recompute(WorldEntity entity, Catalog catalog)
		{
			// Sum each kind of delta over every live buff. The ActiveBuffs key is the WIRE id ((base << 8) | rank).
			var flat = new Dictionary<CharacterStatId, int>();
			var percent = new Dictionary<CharacterStatId, int>();
			//
			// The carrier's LEVEL is what resolves a ramped delta (#599) - the same buff is worth +887 health on a
			// level-20 character and +17500 on a level-80 one - so a level change has to re-run this. It already
			// does: RebaseFromAttributes below is driven off the same entity.Level.
			var level = entity.Level;
			foreach (var buffId in entity.ActiveBuffs.Keys)
			{
				Sum(catalog.BySpellBase, buffId >> 8, flat, level);
				Sum(catalog.PercentBySpellBase, buffId >> 8, percent, level);
			}

			// #584: the ATTRIBUTES are resolved and pushed into the entity FIRST, before the loop below, because the
			// three max pools scale off them - `class base + Σ weight × attribute + class row + flat bonuses` - and
			// only the flat half was ever tracked live. A +10 Constitution buff is worth +80 max health on an
			// Assassin and was worth 0, because SetMaxFromBonus re-applied the flat term on top of an attribute term
			// frozen at world entry. Doing it inside the loop would not fix it: HonoredStats is a dictionary, the
			// max-pool handlers come before the attributes in it, and they would read yesterday's Constitution.
			var attributes = VitalAttributes.From(entity.ClassBase, new Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.Strength] = Buffed(CharacterStatId.Strength),
				[CharacterStatId.Constitution] = Buffed(CharacterStatId.Constitution),
				[CharacterStatId.Intelligence] = Buffed(CharacterStatId.Intelligence),
				[CharacterStatId.Wisdom] = Buffed(CharacterStatId.Wisdom),
				[CharacterStatId.Dexterity] = Buffed(CharacterStatId.Dexterity)
			});

			var rebased = false;
			if (entity.Attributes != attributes)
			{
				entity.Attributes = attributes;
				// The rebase moves the BASE only; the loop below re-applies each pool's flat gear/buff term on top of
				// it through SetMaxFromBonus, which is the same order world entry uses.
				rebased = entity.Vitals?.RebaseFromAttributes(entity.CharacterClass, (ushort)entity.Level, attributes, entity.ClassBase) ?? false;
			}

			var changed = new List<(CharacterStatId Stat, uint Value)>();
			foreach (var (stat, apply) in HonoredStats)
			{
				var value = (uint)Buffed(stat);

				// Genuinely unchanged since the last sync - nothing to apply and nothing to report. A max pool is the
				// exception when the attribute rebase above moved its BASE: the flat bonus is what this compares, and
				// it is unchanged precisely in the case that matters (a +Constitution buff carries no HealthMax), so
				// skipping here would drop the new ceiling on the floor.
				var seeded = entity.BuffedStats.TryGetValue(stat, out var previous);
				if (seeded && previous == value && !(rebased && stat is CharacterStatId.HealthMax or CharacterStatId.ManaMax or CharacterStatId.StaminaMax))
					continue;

				// ⚠️ APPLY first, and unconditionally. This used to `continue` before applying whenever the stat was
				// gear-only (unseeded, no flat delta, no percent), which was safe while GearBonuses was fixed at world
				// entry - it never changed, so "gear-only" did mean "unchanged". Equipping now refreshes the aggregate
				// mid-session (#473), and under the old order a gear change to a pool was skipped BEFORE
				// SetMaxFromBonus ran and the entry was never seeded, so it was skipped for ever: a +25 HP item moved
				// the server's max by nothing at all.
				entity.BuffedStats[stat] = value;
				var applied = apply?.Invoke(entity, value) ?? value;

				// The FIRST seed off gear alone is not news - it is what the entity already had at world entry - so it
				// applies but is not reported. Only a later change is.
				if (!seeded && flat.GetValueOrDefault(stat) == 0 && percent.GetValueOrDefault(stat) == 0)
					continue;

				changed.Add((stat, applied));
			}

			return changed;

			// The percentage scales the flat total, which is what "+2% Critical Rating" means - the elixir is worth
			// more on a well-geared character. Percentages ADD to each other rather than compounding: two +2% buffs
			// are +4%, not +4.04%. Truncated, like every other client-side percentage read (see the regen sheet's
			// integer divide in #442) - a percentage of 0 leaves the value bit-identical either way. A debuff can only
			// take a stat to 0, never negative.
			int Buffed(CharacterStatId stat)
			{
				var total = entity.GearBonuses.GetValueOrDefault(stat) + flat.GetValueOrDefault(stat);
				var pct = percent.GetValueOrDefault(stat);
				if (pct != 0)
					total = (int)((long)total * (PercentScale + pct) / PercentScale);

				return total > 0
					? total
					: 0;
			}

			static void Sum(IReadOnlyDictionary<uint, Delta[]> from, uint spellBase, Dictionary<CharacterStatId, int> into, uint level)
			{
				if (!from.TryGetValue(spellBase, out var deltas))
					return;

				foreach (var delta in deltas)
					into[delta.Stat] = into.GetValueOrDefault(delta.Stat) + delta.At(level);
			}
		}

		// The scale a FLAT spell amount is stored at. It is the stat's wire scale - the same one item modifiers use -
		// EXCEPT for attributes, which spells store in display units: "Potion of Strength" carries 0x324 = 2 and the
		// client's own description for the sibling 0x32E records reads "Augmente la Dextérité de 1/2", so a ×10 read
		// would make them +0.2 Strength. Items really do store attributes ×10; the two sources differ here, which is
		// why this is not just CharacterStatId.GetScale().
		internal static uint SpellScale(CharacterStatId stat)
		{
			return stat is CharacterStatId.Strength or CharacterStatId.Constitution or CharacterStatId.Intelligence or CharacterStatId.Wisdom or CharacterStatId.Dexterity
				? 1u
				: stat.GetScale();
		}

		private async Task<Catalog> EnsureLoaded()
		{
			if (_catalog != null)
				return _catalog.Value;

			await _loadLock.WaitAsync();
			try
			{
				if (_catalog != null)
					return _catalog.Value;

				var rows = await _database.GetSpellStatEffects();
				var catalog = BuildCatalog(rows);
				_catalog = catalog;
				_logger.LogInformation(
					"BuffStatEffects: {Spells} spell(s) with flat stat effects and {PercentSpells} with percent ones, out of {Rows} imported records " +
					"({Percent} percent-kind on unmodelled stats, {Kind} other kinds, {Stat} unmodelled stats - see #367).",
					catalog.BySpellBase.Count, catalog.PercentBySpellBase.Count, rows.Length, catalog.PercentSkipped, catalog.KindSkipped, catalog.StatSkipped);
				return catalog;
			}
			finally
			{
				_loadLock.Release();
			}
		}

		// One honored delta a spell carries: which stat, and what it is worth. Not a bare amount because a delta is
		// either a constant or a LEVEL RAMP (#599), and only the carrier's level tells the two apart at the point of
		// use - see <see cref="Delta.At" />.
		internal readonly record struct Delta(CharacterStatId Stat, int Amount, LevelRamp? Ramp = null)
		{
			// The delta's value for a carrier of this level, in the units the catalog stores for this kind: DISPLAY
			// units for a flat row (already divided at build time), raw hundredths for a percent one.
			//
			// A ramp interpolates in the RAW units it was decoded in and divides afterwards, which is why it is the
			// one shape the build could not pre-convert.
			public int At(uint level)
			{
				return Ramp is { } ramp
					? ramp.ValueAt(level) / (int)SpellScale(Stat)
					: Amount;
			}
		}

		// The honored deltas per spell base id - flat ones in DISPLAY units, percent ones raw (x100 of a percent) -
		// plus what the filter dropped and why.
		internal readonly record struct Catalog(
			IReadOnlyDictionary<uint, Delta[]> BySpellBase,
			IReadOnlyDictionary<uint, Delta[]> PercentBySpellBase,
			int PercentSkipped,
			int KindSkipped,
			int StatSkipped);
	}
}
