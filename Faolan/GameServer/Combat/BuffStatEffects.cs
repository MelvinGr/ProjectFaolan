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
		// Build the flat/percent/derived stat-effect catalog now (#938): 25k rows, ~400 ms, and lazily built on the
		// first buff resolution otherwise - so the first player to be hit or to cast paid it. WarmAsync moves it to
		// startup for every subsystem that shares it.
		public async Task WarmAsync() => await EnsureLoaded();

		// The PERCENT kind: the Amount is a percentage x100, so 200 is "+2%" - confirmed against the client's own elixir
		// descriptions, whose 2%/5% tiers carry 200/500 and 10%/20% carry 1000/2000. It scales a computed TOTAL rather
		// than adding to it, which is why it needed a model of its own (#436).
		internal const uint PercentKind = 53067;

		// ⭐ #807: `AddPercentSkillModifier`, the OTHER percent kind, and the one whose rows this table used to delete.
		// It states "raise stat B by N% of stat A" and, on 86 of the catalog's 106 elements, A and B are the SAME stat
		// - which is the 53067 model exactly, at a different scale.
		internal const uint PercentOfStatKind = ItemTemplateStatModifiers.PercentOfStatKind;

		// ...and that scale is the whole reason the two cannot share a constant: 53067 stores percent x100 (200 = 2%)
		// while 53189 stores a PLAIN percent (8 = 8%, measured across its whole population - 1..20). Converted on the
		// way into the catalog rather than at the row, so the table keeps the template's own number.
		private const int PercentOfStatToHundredths = 100;

		// A percentage's denominator: 10000 == +100%. Retail uses the same x10000 scale for the multiplier STATS it
		// keeps a percentage in - a live read of the fatality buff shows stat 536 "Magic Damage Modifier" moving
		// 10000 -> 12000 for a +20% effect, i.e. 10000 IS 1.00.
		private const int PercentScale = 10000;

		// The kinds whose Amount ADDS to the stat (as opposed to a percent modifier or an instant pool move). Classified
		// empirically over every imported record: these carry values that read as flat in their own units against
		// the spell's rank ladder and the client's description text (Fend +1000 Armor / +500 Protection, Battle Cry
		// +62 Hit Rating, "Augmente la Dextérité de 1" for the 0x32E=1 record).
		//
		// 53189 `AddPercentSkillModifier` is not here, and is not missing either - it is a PERCENT kind, handled
		// beside 53067 (#807). It carries no key 0, so under the old positional read its rows were applied to whatever
		// word sat at slot 0, the by-key fix stopped storing them, and for a while a test gated the kind out of the
		// table entirely. What changed is that the client named its two stats: its tooltip branch reads it as
		// `key 67 += key 39 % of key 66`, so the row has a target (`StatId`), a source (`SourceStatId`) and a
		// percentage. When the two stats are EQUAL - 85 of the 105 shipped rows - it is a percent of the stat itself
		// and goes down the 53067 path; when they differ it is a DERIVED stat this engine cannot express, and
		// `Gate.DerivedFromOtherStat` counts it rather than guessing.
		//
		// ⚠️ 53220/53221 are additive but NOT flat: they are a LEVEL RAMP whose Amount is only the low endpoint, and
		// the row's three ramp columns carry the rest (#599). Honored through LevelRamp at the carrier's level - see
		// Delta.At. Their percent twin 53068 `InterpolatedModifyAsRating` is deliberately absent, for the same reason
		// 53067 is: a percentage is not an addition.
		//
		// ⚠️ 53110 / 53221 are NOT on this list - they are OVERRIDES, see OverrideKinds. Everything the argument below
		// establishes about them still holds; it is about the row's UNIT and SHAPE, which is a different question from
		// how the value COMBINES with what is already on the stat.
		//
		// 53110 `TempChangeSkill` is `Modify` with a duration in its name, and four things say so rather than one:
		//   - its KEY LIST is `[0 Stat, 39 Value]`, character-for-character the one 53045 `Modify` carries
		//     (GameActionTypes) - so the record is the same shape read the same way;
		//   - its own RAMPED twin 53221 `InterpolatedTempChangeSkill` was already on this list, classified as a flat
		//     ramp off `Runed Flesh`'s level-tiled ranks. 53221 is to 53110 what 53220 is to 53045, so honoring the
		//     interpolated half while calling the plain half "unclassified" was the list disagreeing with itself;
		//   - on the stats whose unit is already proven its amounts land in THAT unit, not a rescaled one:
		//     `Skin of Iron` carries 10000 on slashing/piercing/crushing/poison invulnerability, the same +100% step
		//     `Magic Shield` states with 53045 on cold, and `Soul of Mitra` carries 90 damage deflection next to
		//     `Phoenix Cloak`'s 53045 +40/+50;
		//   - `Soul of Mitra`'s own client description - "shielding the priest from almost all damage" - names the
		//     effect that deflection row expresses, which was landing nowhere.
		// Its other 356 rows are a POLYMORPH tail (Head Mesh, Monster Data, Scale, Super Family) plus Natural Energy
		// Regeneration, and the stat gate below goes on holding those: a shapeshift is not `gear + sum(buffs)`.
		private static readonly HashSet<uint> FlatKinds = [53045, 53220];

		// The kinds whose Amount REPLACES the stat instead of adding to it (#805). Two, and they are the pair the
		// paragraph above admitted: the client's own executor for `53110 TempChangeSkill` is
		// `SetStatOverride(statTable, Stat, Value)` and its remove is `ClearStatOverride(statTable, Stat)`; `53221
		// InterpolatedTempChangeSkill` is the same call with the value lerped between the ramp's endpoints
		// (docs/re/gameaction-executors.tsv, #709). An override, not an addition.
		//
		// ⭐ This is not a free choice of combine rule, because the CLIENT RUNS THAT EXECUTOR ON OUR OWN BUFF PACKET.
		// The chain is `MagicBuffIIR_c::Activate` (`FUN_007a1580`, named by its own log line "MagicBuffIIR_c::Activate,
		// UpdateBuff with parameters:/n BuffTemplate ") -> `CoreMagicHolder::UpdateBuff` (`FUN_007ee710`) ->
		// `RunGameActions` (`FUN_0089bab0`) -> the executor's vtable slot 9. So the very packet that applies a buff
		// makes the client evaluate the spell's elements from its OWN RDB copy, and for these two it sets an override
		// on the stat. A server that pushed `gear + amount` would be a second opinion on a number the client had
		// already decided, and the two would disagree on exactly the stats that also carry gear.
		//
		// ⚠️ Two overrides on one stat: the client keeps ONE override slot per stat, so whichever landed last wins -
		// an order our ActiveBuffs dictionary does not have and could not honor stably. The strongest (largest
		// magnitude) wins here instead: deterministic, and it is the reading that cannot be beaten by a weaker buff
		// re-landing. An assumption, not a capture fact - no capture has two override buffs on one stat.
		private static readonly HashSet<uint> OverrideKinds = [53110, 53221];

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
		private static readonly Dictionary<CharacterStatId, Func<WorldEntity, uint, uint>?> HonoredStats = new Dictionary<CharacterStatId, Func<WorldEntity, uint, uint>?>
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
			// KnockbackResistance is the one with a live consumer: `WorldBroadcaster.Knock` already reads it off
			// `BuffedStats` and falls back to the gear bonus, so honoring it here is what makes a knockback-resisting
			// BUFF actually resist one rather than only a knockback-resisting item.
			//
			// ⭐ The client's own name for `0x40D` is **`Weight`** (#625 - the exe's descriptor registry, which beats
			// the LDB's `Knockback Resistance`), and that is not a contradiction, it is the MECHANISM: mass is how a
			// physics engine resists a shove. Retail's own data agrees from the other end - `Turtle Formation` grants
			// +100 of it, which is a stance making you harder to move. The name here stays `KnockbackResistance`
			// because it is what our model uses the number FOR, and because `0x40D` never crosses the wire in either
			// direction, so nothing the client draws depends on the label.
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

		private readonly ICatalogRepository _database;
		private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
		private readonly ILogger _logger;

		// The honored flat + percent deltas per spell BASE id. Loaded once (1.7k rows) on first use.
		private Catalog? _catalog;

		public BuffStatEffects(ICatalogRepository database, ILogger logger)
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
				? [.. Applicable(deltas, level).Select(d => (d.Stat, d.At(level)))]
				: [];
		}

		// The deltas that apply at this level, out of a set that may state the same stat once per level BAND (#783).
		// Resolved PER STAT: two deltas on different stats are two effects, while two on one stat are the same effect
		// said once per band - and where they are NOT banded (a record stating a stat twice outright) every one of them
		// survives, which is `SpellEffectBands`' rule rather than a second one here.
		private static IReadOnlyList<Delta> Applicable(IReadOnlyList<Delta> deltas, uint level)
		{
			if (deltas.Count <= 1)
				return deltas;

			// ⚠️ Grouped by (target, SOURCE), not by target alone. For everything but a derived row `SourceStat` is
			// null and the key degenerates to the stat, so this is the same grouping it always was - but a derived
			// spell states one term PER SOURCE at each rank (`Will of the Sublime` is 5 ranks x Intelligence and
			// Strength), and banding those together picks one band across both and drops the other source (#807).
			var applicable = new List<Delta>(deltas.Count);
			foreach (var key in deltas.Select(d => (d.Stat, d.SourceStat)).Distinct())
				applicable.AddRange(SpellEffectBands.ForLevel([.. deltas.Where(d => (d.Stat, d.SourceStat) == key)], level, d => d.Band));

			return applicable;
		}

		// Recomputes `gear + active buffs` for every honored stat on this entity, writes the live fields, and returns
		// the stats whose value CHANGED with their new display value (so the caller can push exactly those). Empty when
		// nothing moved - the common case, since most buffs carry no honored deltas.
		public async Task<IReadOnlyList<(CharacterStatId Stat, uint Value)>> Sync(WorldEntity entity)
		{
			return Recompute(entity, await EnsureLoaded());
		}

		// Whether an imported row reaches the live stat engine, and when it does not, WHICH of the two gates stopped
		// it. Public because the imported-vs-applied gap gets counted from outside the server (`--report
		// spell-effect-gap`), and a census that re-implements the gate is how #701 came to be filed off a figure that
		// was four times the real one and pointed at the wrong half of the problem. There is one policy; it lives here.
		// Whether this stat is one the engine can express at all - the second half of Classify, on its own, because
		// "how many of the rows a kind drops land on a stat we already model" is what says whether admitting that
		// kind is worth anything, and that question is about the stat with the kind held aside.
		public static bool Models(CharacterStatId stat)
		{
			return HonoredStats.ContainsKey(stat);
		}

		public static Gate Classify(SpellStatEffect row)
		{
			// ⚠️ 53189 is classified FIRST and on its own, because whether it is usable turns on a column no other
			// kind sets. Same source and target is a percent of the stat itself and goes down the 53067 path; a
			// DIFFERENT source is a DERIVED stat - "+3% of your Intelligence onto your Critical Damage Rating" - and
			// needs BOTH ends to be resolvable, so it is gated twice.
			//
			// ⚠️ This used to refuse every derived row outright, on the stated grounds that "both stats the shipped
			// rows derive onto (490 ImmunityRating, 711 CriticalDamageBonusRating) are unmodelled anyway". Half of
			// that was wrong: 711 is 0x2C7 = `CriticalDamageRating`, which IS honored and IS consumed by the combat
			// resolver - so `Will of the Sublime`'s ten rows were being dropped for a reason that did not apply to
			// them. `Arcane Plunder`'s ten really do target an unmodelled stat and still fall at the target gate.
			if (row.EffectKind == PercentOfStatKind)
			{
				if (!HonoredStats.ContainsKey(row.StatId))
					return Gate.PercentOnUnmodelledStat;

				if (row.SourceStatId == row.StatId)
					return Gate.Percent;

				// A derived term reads the SOURCE's resolved value, so a source we do not resolve is as fatal as an
				// unmodelled target - and it is a different drop, so it says so.
				return row.SourceStatId is { } source && (HonoredStats.ContainsKey(source) || VitalAttributes.IsAttribute(source))
					? Gate.Derived
					: Gate.DerivedFromOtherStat;
			}

			var isPercent = row.EffectKind == PercentKind;
			if (!isPercent && !FlatKinds.Contains(row.EffectKind) && !OverrideKinds.Contains(row.EffectKind))
				return Gate.UnhandledKind;

			// The stat gate is the same for both kinds: a percentage of a stat whose scale we have not verified is
			// no more usable than a flat amount of it.
			if (!HonoredStats.ContainsKey(row.StatId))
				return isPercent ? Gate.PercentOnUnmodelledStat : Gate.UnmodelledStat;

			return isPercent ? Gate.Percent : Gate.Flat;
		}

		// The catalog build, as a pure function over the imported rows: filter to what we honor and convert each
		// amount to display units. Also reports what was dropped, so the load summary can say how big the gap is.
		internal static Catalog BuildCatalog(IEnumerable<SpellStatEffect> rows,
			ILookup<(uint SpellId, uint Sequence), SpellStatEffectCriterion>? criteria = null)
		{
			var honored = new Dictionary<uint, List<Delta>>();
			var derivedOf = new Dictionary<uint, List<Delta>>();
			var percent = new Dictionary<uint, List<Delta>>();
			int percentSkipped = 0, kindSkipped = 0, statSkipped = 0, derivedSkipped = 0;
			// ⚠️ `DistinctBy(Statement)`: a record states the same element twice (`Hellfire Stream` repeats two of its
			// five bands verbatim), and a repeated statement is one statement - see `SpellStatEffect.Statement`.
			foreach (var row in rows.DistinctBy(r => r.Statement))
			{
				switch (Classify(row))
				{
					case Gate.UnhandledKind:
					{
						kindSkipped++;
						continue;
					}

					case Gate.PercentOnUnmodelledStat:
					{
						percentSkipped++;
						continue;
					}

					case Gate.UnmodelledStat:
					{
						statSkipped++;
						continue;
					}

					case Gate.DerivedFromOtherStat:
					{
						derivedSkipped++;
						continue;
					}
				}

				var isPercent = row.EffectKind is PercentKind or PercentOfStatKind;

				// A percentage is NOT in the stat's units - it is x100 of a percent - so it is carried raw, while a
				// flat amount converts to display units here.
				//
				// ⚠️ A RAMPED row carries BOTH endpoints raw and converts AFTER the interpolation instead (Delta.At),
				// because the lerp is the client's float arithmetic over the raw endpoints: dividing them first
				// rounds twice, and the two disagree by one over most of a ramp's span.
				var ramp = !isPercent && row is { HighAmount: { } high, LowLevel: { } low, HighLevel: { } top }
					? new LevelRamp(row.Amount, high, low, top)
					: (LevelRamp?)null;
				// ⭐ #791: the element's own CONDITIONS ride with the delta, so the decision to apply it is made at
				// the point of use - where the carrier's live stats exist - rather than at load, where they do not.
				var conditions = criteria?[(row.SpellId, row.Sequence)]
					.Select(c => new ItemTemplateStatModifiers.Criterion(c.CriterionId, c.Value, c.Operator))
					.ToArray();
				// 53189's percentage is PLAIN where 53067's is x100, so it converts here - the one place that knows
				// both conventions - rather than either scale leaking into the table or into `Buffed`.
				var amount = row.EffectKind == PercentOfStatKind
					? row.Amount * PercentOfStatToHundredths
					: isPercent || ramp is not null
						? row.Amount
						: row.Amount / (int)SpellScale(row.StatId);

				var derived = Classify(row) == Gate.Derived;
				var delta = new Delta(row.StatId, amount, ramp, conditions is { Length: > 0 } ? conditions : null,
					OverrideKinds.Contains(row.EffectKind), derived ? row.SourceStatId : null);

				// A DERIVED row is a percentage too, but of a DIFFERENT stat - so it cannot join `percent`, whose
				// entries all scale their own stat's total. Its own table, read by `Buffed` after that scaling.
				var into = derived ? derivedOf : isPercent ? percent : honored;
				(into.TryGetValue(row.SpellId, out var list) ? list : into[row.SpellId] = []).Add(delta);
			}

			return new Catalog(honored.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
				percent.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), percentSkipped, kindSkipped, statSkipped, derivedSkipped,
				derivedOf.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
		}

		// The recompute, as a pure function over an entity + a catalog: `(gear + flat buffs) x (1 + percent buffs)` per
		// honored stat, written to the live fields, returning only what changed. Separate from Sync so it is testable
		// without a database.
		internal static IReadOnlyList<(CharacterStatId Stat, uint Value)> Recompute(WorldEntity entity, Catalog catalog)
		{
			// Sum each kind of delta over every live buff. The ActiveBuffs key is the WIRE id ((base << 8) | rank).
			var flat = new Dictionary<CharacterStatId, int>();
			var percent = new Dictionary<CharacterStatId, int>();
			// …except an OVERRIDE, which does not sum with anything (#805): it replaces the gear + flat total for its
			// stat, the way the client's own `SetStatOverride` executor does.
			var overrides = new Dictionary<CharacterStatId, int>();
			// #807: "raise stat B by N% of stat A". Keyed by the TARGET, because that is what `Buffed` is asked for;
			// each entry names the source it reads and the percentage of it to add.
			var derived = new Dictionary<CharacterStatId, List<(CharacterStatId Source, int Percent)>>();
			//
			// The carrier's LEVEL is what resolves a ramped delta (#599) - the same buff is worth +887 health on a
			// level-20 character and +17500 on a level-80 one - so a level change has to re-run this. It already
			// does: RebaseFromAttributes below is driven off the same entity.Level.
			var level = entity.Level;
			foreach (var buffId in entity.ActiveBuffs.Keys)
			{
				Sum(catalog.BySpellBase, buffId >> 8, flat, level);
				Sum(catalog.PercentBySpellBase, buffId >> 8, percent, level);
				SumDerived(catalog.DerivedBySpellBase, buffId >> 8, level);
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
				if (!seeded && flat.GetValueOrDefault(stat) == 0 && percent.GetValueOrDefault(stat) == 0 && !overrides.ContainsKey(stat))
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
				var total = Own(stat);

				// #807: a DERIVED term adds a percentage of ANOTHER stat. It reads that stat's OWN value rather than
				// its `Buffed` one, which is what keeps this non-recursive - and is right on every shipped row, whose
				// sources are all base attributes and so carry no derived term of their own. A cycle would otherwise
				// be expressible in data we do not control.
				if (derived.TryGetValue(stat, out var terms))
				{
					foreach (var (source, pct) in terms)
						total += (int)((long)Own(source) * pct / PercentScale);
				}

				return total > 0
					? total
					: 0;
			}

			// One stat's own total: the gear + flat base (or an override) scaled by its own percentages, and nothing
			// derived from anywhere else.
			int Own(CharacterStatId stat)
			{
				// An override replaces the gear + flat base and nothing else: a percentage still scales the result,
				// because a percent record is a separate element on a separate stat table entry either way.
				var total = overrides.TryGetValue(stat, out var overridden)
					? overridden
					: entity.GearBonuses.GetValueOrDefault(stat) + flat.GetValueOrDefault(stat);
				var pct = percent.GetValueOrDefault(stat);
				if (pct != 0)
					total = (int)((long)total * (PercentScale + pct) / PercentScale);

				return total;
			}

			void SumDerived(IReadOnlyDictionary<uint, Delta[]>? from, uint spellBase, uint carrierLevel)
			{
				if (from == null || !from.TryGetValue(spellBase, out var deltas))
					return;

				foreach (var delta in Applicable(deltas, carrierLevel))
				{
					if (delta.SourceStat is not { } source)
						continue;

					(derived.TryGetValue(delta.Stat, out var terms) ? terms : derived[delta.Stat] = []).Add((source, delta.Amount));
				}
			}

			void Sum(IReadOnlyDictionary<uint, Delta[]> from, uint spellBase, Dictionary<CharacterStatId, int> into, uint level)
			{
				if (!from.TryGetValue(spellBase, out var deltas))
					return;

				// Through the same band resolution ForSpellBase uses, so the sheet and the live stats never disagree
				// about which band a buff is in.
				foreach (var delta in Applicable(deltas, level))
				{
					// ⭐ #791: a delta that states CONDITIONS applies only when they hold. The carrier is both subject
					// and target here - a live buff's stat contribution is read off the entity wearing it, and the
					// entity that cast it is long gone by the time this recomputes.
					if (delta.Criteria is { } conditions && !CriteriaEvaluator.Permits(conditions, StatOf, StatOf, out _))
						continue;

					var value = delta.At(level);
					if (!delta.Override)
					{
						into[delta.Stat] = into.GetValueOrDefault(delta.Stat) + value;
						continue;
					}

					// Strongest wins - see OverrideKinds for why this is a choice and not a reading.
					if (!overrides.TryGetValue(delta.Stat, out var already) || Math.Abs(value) > Math.Abs(already))
						overrides[delta.Stat] = value;
				}
			}

			// The carrier's own sheet, as the evaluator wants it: the live buffed value where one exists, the level
			// and the class from the entity itself. An unknown stat reads 0, which is what an entity that does not
			// carry one reports.
			int? StatOf(CharacterStatId stat)
			{
				// ⚠️ NULL, not 0, for a stat this entity cannot supply - the evaluator treats that as unevaluated and
				// lets the effect through. Reading an absent stat as 0 would drop every effect gated on `> n`.
				return stat switch
				{
					CharacterStatId.Level => (int)entity.Level,
					CharacterStatId.HealthCurrent => (int?)entity.Vitals?.Health,
					_ => entity.BuffedStats.TryGetValue(stat, out var value) ? (int)value : null
				};
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

				var clock = System.Diagnostics.Stopwatch.StartNew();
				var rows = await _database.GetSpellStatEffects();
				var criteria = await _database.GetSpellStatEffectCriteria();
				var catalog = BuildCatalog(rows, criteria);
				_catalog = catalog;
				_logger.LogInformation(
					"BuffStatEffects: {Spells} spell(s) with flat stat effects, {PercentSpells} with percent ones and {DerivedSpells} with a stat DERIVED from another, " +
					"out of {Rows} imported records " +
					"({Percent} percent-kind on unmodelled stats, {Kind} other kinds, {Stat} unmodelled stats, " +
					"{Derived} derived from a stat we do not resolve - see #367/#807); " +
					"{Conditional} honored delta(s) carry CONDITIONS and are evaluated per carrier (#791). Built in {Ms} ms.",
					catalog.BySpellBase.Count, catalog.PercentBySpellBase.Count, catalog.DerivedBySpellBase?.Count ?? 0, rows.Length, catalog.PercentSkipped, catalog.KindSkipped, catalog.StatSkipped,
					catalog.DerivedSkipped,
					catalog.BySpellBase.Values.Concat(catalog.PercentBySpellBase.Values).Sum(d => d.Count(x => x.Criteria != null)), clock.ElapsedMilliseconds);
				return catalog;
			}
			finally
			{
				_loadLock.Release();
			}
		}

		// The gate's answer for one imported row - honored, or dropped and by which half. The two dropped-stat members
		// are kept apart rather than merged because they are different work: a PERCENT record on an unmodelled stat
		// needs the stat admitted, while a flat one may need only that too - the load summary has always reported them
		// separately and the split is what says which.
		public enum Gate
		{
			Flat,
			Percent,
			UnhandledKind,
			UnmodelledStat,
			PercentOnUnmodelledStat,

			// 53189 with a source stat DIFFERENT from its target, and BOTH ends resolvable - "raise stat B by N% of
			// stat A" (#807).
			Derived,

			// The same shape with a SOURCE we cannot resolve. Its own member rather than folded into UnhandledKind,
			// because the kind IS handled: it is this row's source that is not, and the load summary should say which
			// of the two ends it dropped.
			DerivedFromOtherStat
		}

		// One honored delta a spell carries: which stat, and what it is worth. Not a bare amount because a delta is
		// either a constant or a LEVEL RAMP (#599), and only the carrier's level tells the two apart at the point of
		// use - see <see cref="Delta.At" />.
		// <paramref name="Override" /> is #805: the delta REPLACES the stat rather than adding to it, because the
		// client's own executor for the kind is `SetStatOverride` - see OverrideKinds.
		internal readonly record struct Delta(CharacterStatId Stat, int Amount, LevelRamp? Ramp = null,
			IReadOnlyList<ItemTemplateStatModifiers.Criterion>? Criteria = null, bool Override = false,
			CharacterStatId? SourceStat = null)
		{
			// The LEVEL BAND this delta states its numbers for, or null when it is level-independent - the ramp's own
			// two levels, because a record that bands a stat bands it by ramping each band separately (#783).
			public (uint Low, uint High)? Band => Ramp is { } ramp ? (ramp.LowLevel, ramp.HighLevel) : null;

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
			int StatSkipped,
			int DerivedSkipped = 0,
			IReadOnlyDictionary<uint, Delta[]>? DerivedBySpellBase = null);
	}
}
