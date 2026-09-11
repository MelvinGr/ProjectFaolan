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
	/// <summary>
	///     The buff stat engine: what a live buff does to the entity carrying it, beyond rendering an icon.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A spell's stat deltas live in its RDB template's [2][9] region (<see cref="SpellStatEffect" />, imported
	///         per spell as (kind, stat, amount)). This turns the justifiable ones into live entity stats: on every
	///         change to an entity's <c>ActiveBuffs</c>, the affected stats are recomputed as
	///         <c>gear + sum(active buffs)</c> and the changed ones pushed to the client. Recomputed, never
	///         incremented - an add/subtract pair drifts the moment a buff is refreshed, re-applied on relog, or
	///         cleared by death, and a drifted armor rating is invisible until combat feels wrong.
	///     </para>
	///     <para>
	///         A record is honored only when both gates pass:
	///     </para>
	///     <list type="bullet">
	///         <item>
	///             its kind must combine additively (<see cref="FlatKinds" />) or be one of the modelled percent
	///             kinds. The same region also carries percent records (kind 53067: Philtre of Guile 0x138 = 200 is
	///             "+2% Critical Rating") and health effects (kind 53219: an instant heal, or the -100000000 drain
	///             sentinel). Reading a percent record as flat turns +2% into +200, so an unknown kind is never
	///             honored.
	///         </item>
	///         <item>
	///             the stat must be one this engine can express - see <see cref="HonoredStats" /> for that criterion.
	///         </item>
	///     </list>
	///     <para>
	///         Everything else stays imported but inert and is counted in the load summary, so the gap is visible
	///         rather than silently applied with a guessed scale. docs/rdb/spell-stat-effects.md has the kind
	///         classification and its evidence.
	///     </para>
	///     <para>
	///         This is not a general spell-effect dispatcher. Pet summons, mounts, the Shield of the Risen, spellweave
	///         trances and the fatality proc are their own paths (<see cref="SpellEffectService" /> for the
	///         data-driven ones, <c>MountCatalog</c> / <c>SpellweaveCatalog</c> for the mount- and class-keyed ones).
	///         This owns one question - what an active buff adds to its carrier's stats - and answers it from data.
	///     </para>
	/// </remarks>
	public sealed class BuffStatEffects
	{
		/// <summary>
		///     Builds the flat/percent/derived stat-effect catalog eagerly: 25k rows, roughly 400 ms.
		/// </summary>
		/// <remarks>
		///     Without this the catalog is built lazily on the first buff resolution, so the first player to be hit or
		///     to cast pays for it. Call this at startup for every subsystem that shares the catalog.
		/// </remarks>
		public async Task WarmAsync() => await EnsureLoaded();

		/// <summary>
		///     The percent kind: the Amount is a percentage x100, so 200 is "+2%".
		/// </summary>
		/// <remarks>
		///     Confirmed against the client's own elixir descriptions, whose 2%/5% tiers carry 200/500 and 10%/20%
		///     carry 1000/2000. It scales a computed total rather than adding to it, hence its own model.
		/// </remarks>
		internal const uint PercentKind = 53067;

		/// <summary>
		///     <c>AddPercentSkillModifier</c>, the other percent kind: "raise stat B by N% of stat A".
		/// </summary>
		/// <remarks>
		///     On 86 of the catalog's 106 elements A and B are the same stat, which is the <see cref="PercentKind" />
		///     model exactly, at a different scale.
		/// </remarks>
		internal const uint PercentOfStatKind = ItemTemplateStatModifiers.PercentOfStatKind;

		/// <summary>
		///     Converts 53189's plain percent into <see cref="PercentKind" />'s hundredths.
		/// </summary>
		/// <remarks>
		///     The differing scale is why the two percent kinds cannot share a constant: 53067 stores percent x100
		///     (200 = 2%) while 53189 stores a plain percent (8 = 8%, across its whole population of 1..20). Converted
		///     on the way into the catalog rather than at the row, so the table keeps the template's own number.
		/// </remarks>
		private const int PercentOfStatToHundredths = 100;

		/// <summary>
		///     A percentage's denominator: 10000 == +100%.
		/// </summary>
		/// <remarks>
		///     Retail uses the same x10000 scale for the multiplier stats it keeps a percentage in - a live read of the
		///     fatality buff shows stat 536 "Magic Damage Modifier" moving 10000 -> 12000 for a +20% effect, so 10000
		///     is 1.00.
		/// </remarks>
		private const int PercentScale = 10000;

		/// <summary>
		///     The kinds whose Amount adds to the stat, as opposed to a percent modifier, an override or an instant
		///     pool move.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Classified empirically over every imported record: these carry values that read as flat in their own
		///         units against the spell's rank ladder and the client's description text (Fend +1000 Armor / +500
		///         Protection, Battle Cry +62 Hit Rating, "Augmente la Dextérité de 1" for the 0x32E=1 record).
		///     </para>
		///     <para>
		///         53220 is additive but not constant: it is a level ramp whose Amount is only the low endpoint, with
		///         the row's three ramp columns carrying the rest. Honored through <see cref="LevelRamp" /> at the
		///         carrier's level - see <see cref="Delta.At" />. Its percent twin 53068
		///         <c>InterpolatedModifyAsRating</c> is deliberately absent, for the same reason 53067 is: a percentage
		///         is not an addition.
		///     </para>
		///     <para>
		///         Three neighbouring kinds are classified elsewhere rather than missing: 53189
		///         <c>AddPercentSkillModifier</c> is a percent kind (see <see cref="PercentOfStatKind" />), and
		///         53110 / 53221 are overrides (see <see cref="OverrideKinds" />).
		///     </para>
		/// </remarks>
		private static readonly HashSet<uint> FlatKinds = [53045, 53220];

		/// <summary>
		///     The kinds whose Amount replaces the stat instead of adding to it.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The client's own executor for 53110 <c>TempChangeSkill</c> is
		///         <c>SetStatOverride(statTable, Stat, Value)</c>, and its remove is
		///         <c>ClearStatOverride(statTable, Stat)</c>; 53221 <c>InterpolatedTempChangeSkill</c> is the same call
		///         with the value lerped between the ramp's endpoints (docs/re/gameaction-executors.tsv).
		///     </para>
		///     <para>
		///         The combine rule is not a free choice, because the client runs that executor on our own buff packet:
		///         <c>MagicBuffIIR_c::Activate</c> (FUN_007a1580) -&gt; <c>CoreMagicHolder::UpdateBuff</c>
		///         (FUN_007ee710) -&gt; <c>RunGameActions</c> (FUN_0089bab0) -&gt; the executor's vtable slot 9. The
		///         packet that applies a buff makes the client evaluate the spell's elements from its own RDB copy, and
		///         for these two it sets an override on the stat. A server pushing <c>gear + amount</c> would be a
		///         second opinion on a number the client had already decided, and the two would disagree on exactly the
		///         stats that also carry gear.
		///     </para>
		///     <para>
		///         The amounts land in the stat's proven unit rather than a rescaled one: <c>Skin of Iron</c> carries
		///         10000 on slashing/piercing/crushing/poison invulnerability, the same +100% step <c>Magic Shield</c>
		///         states with 53045 on cold, and <c>Soul of Mitra</c> carries 90 damage deflection next to
		///         <c>Phoenix Cloak</c>'s 53045 +40/+50.
		///     </para>
		///     <para>
		///         With two overrides on one stat the client keeps a single override slot per stat, so whichever landed
		///         last wins - an order the <c>ActiveBuffs</c> dictionary does not have and could not honor stably. The
		///         strongest (largest magnitude) wins here instead: deterministic, and the reading that cannot be
		///         beaten by a weaker buff re-landing. That is an assumption, not a capture fact - no capture has two
		///         override buffs on one stat.
		///     </para>
		///     <para>
		///         53110's other 356 rows are a polymorph tail (Head Mesh, Monster Data, Scale, Super Family) plus
		///         Natural Energy Regeneration; the stat gate below drops those, because a shapeshift is not
		///         <c>gear + sum(buffs)</c>.
		///     </para>
		/// </remarks>
		private static readonly HashSet<uint> OverrideKinds = [53110, 53221];

		/// <summary>
		///     The stats a record is applied for: where the live value lands, and what the client is told.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The apply is null for the stats the server does not model yet, which are still pushed so the
		///         character sheet reflects the buff; when it returns a value, that value is pushed instead of the
		///         buffed total (the max pools, where the wire wants the whole pool, not the bonus).
		///     </para>
		///     <para>
		///         The admission criterion is that the stat's baseline is zero, because <see cref="Recompute" />
		///         computes <c>gear + sum(buffs)</c>. Scale is not a criterion - <c>StatDisplayScale</c> transcribes
		///         the client's own divisor rule and covers every id. A multiplier stat therefore does not belong here:
		///         <c>MovementMultiplier</c> (0x0F0) has a baseline of 100 and <c>Turtle Formation</c> carries -50 on
		///         it, so summing from zero would tell the client "movement 50%" for what the buff means as "-50%", and
		///         <c>MagicDamageModifier</c> (0x218) has a baseline of 10000 that a live read shows moving to 12000
		///         for +20%. Those need a model that knows the baseline. Open work tracked in Gitea #700.
		///     </para>
		///     <para>
		///         So: add a stat here when its records add to zero, whatever their scale; leave it off when they scale
		///         a total.
		///     </para>
		/// </remarks>
		private static readonly Dictionary<CharacterStatId, Func<WorldEntity, uint, uint>?> HonoredStats = new Dictionary<CharacterStatId, Func<WorldEntity, uint, uint>?>
		{
			// Gear-consumed ratings - scales proven against in-client item tooltips.
			[CharacterStatId.Armor] = (e, v) => e.ArmorRating = v,
			[CharacterStatId.Protection] = (e, v) => e.Protection = v,
			[CharacterStatId.HitRating] = (e, v) => e.HitRating = v,
			[CharacterStatId.CriticalRating] = (e, v) => e.CriticalRating = v,
			[CharacterStatId.CriticalDamageRating] = (e, v) => e.CriticalDamageRating = v,
			[CharacterStatId.CombatRating] = (e, v) => e.CombatRating = v,
			[CharacterStatId.MagicDamage] = (e, v) => e.MagicDamage = v,
			[CharacterStatId.NaturalManaRegen] = (e, v) => e.NaturalManaRegen = v,
			[CharacterStatId.NaturalStaminaRegen] = (e, v) => e.NaturalStaminaRegen = v,

			// The three max pools. A buff really does raise them - Focused Mind carries stat 507 across its whole rank
			// ladder, Sustaining Wrath stat 1 in 9000-wire steps - and the values are stored at the ordinary x100
			// vitals scale, which the rank ladder forces (Sustaining Wrath's 108000..180000 is +1080..+1800 health;
			// read flat it would be +108000).
			//
			// These go through EntityVitals.SetMaxFromBonus rather than a field write because the pool is
			// `base + gear + buffs` and current has to be clamped when a buff ends and the max comes back down.
			[CharacterStatId.HealthMax] = (e, v) => e.Vitals?.SetMaxFromBonus(CharacterStatId.HealthMax, (int)v) ?? v,
			[CharacterStatId.ManaMax] = (e, v) => e.Vitals?.SetMaxFromBonus(CharacterStatId.ManaMax, (int)v) ?? v,
			[CharacterStatId.StaminaMax] = (e, v) => e.Vitals?.SetMaxFromBonus(CharacterStatId.StaminaMax, (int)v) ?? v,

			// The three resistance stats a buff can move. All three are x1 (docs/subsystems/player-stats.md). Between
			// them they carry 370 imported rows: `Turtle Formation` +100 knockback resistance, `Phoenix Cloak` +40/+50
			// damage deflection, and the `-20 Chance of Being Staggered` every horse's `Mounted` buff carries.
			//
			// KnockbackResistance is the one with a live consumer: KnockbackService reads it off `BuffedStats` and
			// falls back to the gear bonus, so honoring it here is what makes a knockback-resisting buff actually
			// resist one rather than only a knockback-resisting item.
			//
			// The client's own name for 0x40D is `Weight` (the exe's descriptor registry, which beats the LDB's
			// `Knockback Resistance`), which is the mechanism rather than a contradiction: mass is how a physics engine
			// resists a shove, and `Turtle Formation` grants +100 of it as a stance that makes you harder to move. The
			// name here stays `KnockbackResistance` because that is what this model uses the number for, and 0x40D
			// never crosses the wire in either direction, so nothing the client draws depends on the label.
			//
			// The other two are sheet-only: the stat is real and the client draws it, but no server model consults it -
			// there is no stagger mechanic, and damage deflection has no consumer either.
			[CharacterStatId.KnockbackResistance] = null,
			[CharacterStatId.DamageDeflection] = null,
			[CharacterStatId.StaggeringChance] = null,

			// The damage-school invulnerability ratings, all eleven. Additive from a zero baseline, at the client's
			// x100: `Magic Shield` +10000 is +100% cold, `Crom` +20000, `Fire Pit` -20000. Nothing consumes them yet -
			// the mitigation model is per-armour/protection, not per school - but the sheet shows them.
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

			// The out-of-combat regen trio plus natural health regen, completing the 0x383/0x384/0x385 run whose mana
			// and stamina halves are honored above. `Hearty Meal` carries a 264..2640 rank ladder on the health one.
			[CharacterStatId.NaturalHealthRegen] = null,
			[CharacterStatId.NonCombatHealthRegen] = null,
			[CharacterStatId.NonCombatManaRegen] = null,
			[CharacterStatId.NonCombatStaminaRegen] = null,

			// Additive ratings with no consumer yet: `Heightened Sense` +2500 evade (+25%), `Broken Bones` −5000.
			[CharacterStatId.EvadeChance] = null,
			[CharacterStatId.BaseSpellDamage] = null,
			[CharacterStatId.HealthTapAmount] = null,

			// The five attributes: display-unit amounts (see SpellScale), pushed for the sheet. No server model reads
			// them directly - the attribute-driven formulas (armor from Strength, ...) are still client-side.
			[CharacterStatId.Strength] = null,
			[CharacterStatId.Constitution] = null,
			[CharacterStatId.Intelligence] = null,
			[CharacterStatId.Wisdom] = null,
			[CharacterStatId.Dexterity] = null
		};

		private readonly ICatalogRepository _database;
		private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
		private readonly ILogger _logger;

		// The honored flat and percent deltas per spell base id. Loaded once (1.7k rows) on first use.
		private Catalog? _catalog;

		public BuffStatEffects(ICatalogRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		/// <summary>
		///     The honored deltas a buff carries, in display units, resolved for a carrier of the given level - a level
		///     ramp is worth more the higher the carrier.
		/// </summary>
		/// <returns>
		///     Empty for a spell with no stat-effect region, or one whose records are all percent, health or
		///     unmodelled-stat.
		/// </returns>
		public async Task<IReadOnlyList<(CharacterStatId Stat, int Amount)>> ForSpellBase(uint spellBase, uint level)
		{
			var catalog = await EnsureLoaded();
			return catalog.BySpellBase.TryGetValue(spellBase, out var deltas)
				? [.. Applicable(deltas, level).Select(d => (d.Stat, d.At(level)))]
				: [];
		}

		/// <summary>
		///     The deltas that apply at this level, out of a set that may state the same stat once per level band.
		/// </summary>
		/// <remarks>
		///     Resolved per stat: two deltas on different stats are two effects, while two on one stat are the same
		///     effect said once per band. Where they are not banded (a record stating a stat twice outright) every one
		///     survives, which is <see cref="SpellEffectBands" />' rule rather than a second one here.
		/// </remarks>
		private static IReadOnlyList<Delta> Applicable(IReadOnlyList<Delta> deltas, uint level)
		{
			if (deltas.Count <= 1)
				return deltas;

			// Grouped by (target, source), not by target alone. For everything but a derived row `SourceStat` is null
			// and the key degenerates to the stat; a derived spell states one term per source at each rank (`Will of
			// the Sublime` is 5 ranks x Intelligence and Strength), and banding those together would pick one band
			// across both and drop the other source.
			var applicable = new List<Delta>(deltas.Count);
			foreach (var key in deltas.Select(d => (d.Stat, d.SourceStat)).Distinct())
				applicable.AddRange(SpellEffectBands.ForLevel([.. deltas.Where(d => (d.Stat, d.SourceStat) == key)], level, d => d.Band));

			return applicable;
		}

		/// <summary>
		///     Recomputes <c>gear + active buffs</c> for every honored stat on this entity and writes the live fields.
		/// </summary>
		/// <returns>
		///     The stats whose value changed, with their new display value, so the caller can push exactly those.
		///     Empty when nothing moved - the common case, since most buffs carry no honored deltas.
		/// </returns>
		public async Task<IReadOnlyList<(CharacterStatId Stat, uint Value)>> Sync(WorldEntity entity)
		{
			return Recompute(entity, await EnsureLoaded());
		}

		/// <summary>
		///     Whether this stat is one the engine can express at all - the stat half of <see cref="Classify" />, on
		///     its own.
		/// </summary>
		/// <remarks>
		///     Public so a census can ask "how many of the rows a kind drops land on a stat we already model", which is
		///     what says whether admitting that kind is worth anything. That question is about the stat with the kind
		///     held aside.
		/// </remarks>
		public static bool Models(CharacterStatId stat)
		{
			return HonoredStats.ContainsKey(stat);
		}

		/// <summary>
		///     Whether an imported row reaches the live stat engine and, when it does not, which gate stopped it.
		/// </summary>
		/// <remarks>
		///     Public because the imported-versus-applied gap is counted from outside the server
		///     (<c>--report spell-effect-gap</c>). A census that re-implements the gate reports a different figure from
		///     the one the engine acts on; there is one policy, and it lives here.
		/// </remarks>
		public static Gate Classify(SpellStatEffect row)
		{
			// 53189 is classified first and on its own, because whether it is usable turns on a column no other kind
			// sets. Same source and target is a percent of the stat itself and goes down the 53067 path; a different
			// source is a derived stat - "+3% of your Intelligence onto your Critical Damage Rating" - and needs both
			// ends to be resolvable, so it is gated twice.
			if (row.EffectKind == PercentOfStatKind)
			{
				if (!HonoredStats.ContainsKey(row.StatId))
					return Gate.PercentOnUnmodelledStat;

				if (row.SourceStatId == row.StatId)
					return Gate.Percent;

				// A derived term reads the source's resolved value, so an unresolvable source is as fatal as an
				// unmodelled target - and it is a different drop, so it is reported as one.
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

		/// <summary>
		///     The catalog build, as a pure function over the imported rows: filter to the honored ones and convert
		///     each amount to display units.
		/// </summary>
		/// <returns>
		///     The catalog, including counts of what was dropped so the load summary can state the size of the gap.
		/// </returns>
		internal static Catalog BuildCatalog(IEnumerable<SpellStatEffect> rows,
			ILookup<(uint SpellId, uint Sequence), SpellStatEffectCriterion>? criteria = null)
		{
			var honored = new Dictionary<uint, List<Delta>>();
			var derivedOf = new Dictionary<uint, List<Delta>>();
			var percent = new Dictionary<uint, List<Delta>>();
			int percentSkipped = 0, kindSkipped = 0, statSkipped = 0, derivedSkipped = 0;
			// `DistinctBy(Statement)`: a record can state the same element twice (`Hellfire Stream` repeats two of its
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

				// A percentage is not in the stat's units - it is x100 of a percent - so it is carried raw, while a
				// flat amount converts to display units here.
				//
				// A ramped row carries both endpoints raw and converts after the interpolation instead (Delta.At),
				// because the lerp is the client's float arithmetic over the raw endpoints: dividing them first rounds
				// twice, and the two disagree by one over most of a ramp's span.
				var ramp = !isPercent && row is { HighAmount: { } high, LowLevel: { } low, HighLevel: { } top }
					? new LevelRamp(row.Amount, high, low, top)
					: (LevelRamp?)null;
				// The element's own conditions ride with the delta, so the decision to apply it is made at the point of
				// use - where the carrier's live stats exist - rather than at load, where they do not.
				var conditions = criteria?[(row.SpellId, row.Sequence)]
					.Select(c => new ItemTemplateStatModifiers.Criterion(c.CriterionId, c.Value, c.Operator))
					.ToArray();
				// 53189's percentage is plain where 53067's is x100, so it converts here - the one place that knows
				// both conventions - rather than either scale leaking into the table or into `Buffed`.
				var amount = row.EffectKind == PercentOfStatKind
					? row.Amount * PercentOfStatToHundredths
					: isPercent || ramp is not null
						? row.Amount
						: row.Amount / (int)SpellScale(row.StatId);

				var derived = Classify(row) == Gate.Derived;
				var delta = new Delta(row.StatId, amount, ramp, conditions is { Length: > 0 } ? conditions : null,
					OverrideKinds.Contains(row.EffectKind), derived ? row.SourceStatId : null);

				// A derived row is a percentage too, but of a different stat, so it cannot join `percent`, whose
				// entries all scale their own stat's total. It gets its own table, read by `Buffed` after that scaling.
				var into = derived ? derivedOf : isPercent ? percent : honored;
				(into.TryGetValue(row.SpellId, out var list) ? list : into[row.SpellId] = []).Add(delta);
			}

			return new Catalog(honored.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
				percent.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), percentSkipped, kindSkipped, statSkipped, derivedSkipped,
				derivedOf.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
		}

		/// <summary>
		///     The recompute, as a pure function over an entity and a catalog:
		///     <c>(gear + flat buffs) x (1 + percent buffs)</c> per honored stat, written to the live fields.
		/// </summary>
		/// <returns>Only the stats whose value changed.</returns>
		/// <remarks>Separate from <see cref="Sync" /> so it is testable without a database.</remarks>
		internal static IReadOnlyList<(CharacterStatId Stat, uint Value)> Recompute(WorldEntity entity, Catalog catalog)
		{
			// Sum each kind of delta over every live buff. The ActiveBuffs key is the wire id ((base << 8) | rank).
			var flat = new Dictionary<CharacterStatId, int>();
			var percent = new Dictionary<CharacterStatId, int>();
			// ...except an override, which does not sum with anything: it replaces the gear + flat total for its stat,
			// the way the client's own `SetStatOverride` executor does.
			var overrides = new Dictionary<CharacterStatId, int>();
			// "Raise stat B by N% of stat A", keyed by the target because that is what `Buffed` is asked for; each
			// entry names the source it reads and the percentage of it to add.
			var derived = new Dictionary<CharacterStatId, List<(CharacterStatId Source, int Percent)>>();
			// The carrier's level is what resolves a ramped delta - the same buff is worth +887 health on a level-20
			// character and +17500 on a level-80 one - so a level change has to re-run this. It does:
			// RebaseFromAttributes below is driven off the same entity.Level.
			var level = entity.Level;
			foreach (var buffId in entity.ActiveBuffs.Keys)
			{
				Sum(catalog.BySpellBase, buffId >> 8, flat, level);
				Sum(catalog.PercentBySpellBase, buffId >> 8, percent, level);
				SumDerived(catalog.DerivedBySpellBase, buffId >> 8, level);
			}

			// The attributes are resolved and pushed into the entity before the loop below, because the three max pools
			// scale off them - `class base + sum(weight x attribute) + class row + flat bonuses`. A +10 Constitution
			// buff is worth +80 max health on an Assassin, and SetMaxFromBonus re-applies the flat term on top of the
			// attribute term, so the attribute term has to be current first. Doing it inside the loop would not work:
			// HonoredStats is a dictionary whose max-pool handlers come before the attributes, so they would read the
			// previous Constitution.
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
				// The rebase moves the base only; the loop below re-applies each pool's flat gear/buff term on top of
				// it through SetMaxFromBonus, which is the same order world entry uses.
				rebased = entity.Vitals?.RebaseFromAttributes(entity.CharacterClass, (ushort)entity.Level, attributes, entity.ClassBase) ?? false;
			}

			var changed = new List<(CharacterStatId Stat, uint Value)>();
			foreach (var (stat, apply) in HonoredStats)
			{
				var value = (uint)Buffed(stat);

				// Genuinely unchanged since the last sync - nothing to apply and nothing to report. A max pool is the
				// exception when the attribute rebase above moved its base: the flat bonus is what this compares, and
				// it is unchanged precisely in the case that matters (a +Constitution buff carries no HealthMax), so
				// skipping here would drop the new ceiling.
				var seeded = entity.BuffedStats.TryGetValue(stat, out var previous);
				if (seeded && previous == value && !(rebased && stat is CharacterStatId.HealthMax or CharacterStatId.ManaMax or CharacterStatId.StaminaMax))
					continue;

				// Apply first, and unconditionally, even for a gear-only stat: equipping refreshes GearBonuses
				// mid-session, so skipping the apply before SetMaxFromBonus runs would leave the entry unseeded and
				// therefore skipped for ever - a +25 HP item would move the server's max by nothing at all.
				entity.BuffedStats[stat] = value;
				var applied = apply?.Invoke(entity, value) ?? value;

				// The first seed off gear alone is what the entity already had at world entry, so it applies but is not
				// reported. Only a later change is.
				if (!seeded && flat.GetValueOrDefault(stat) == 0 && percent.GetValueOrDefault(stat) == 0 && !overrides.ContainsKey(stat))
					continue;

				changed.Add((stat, applied));
			}

			return changed;

			// The percentage scales the flat total, which is what "+2% Critical Rating" means - the elixir is worth
			// more on a well-geared character. Percentages add to each other rather than compounding: two +2% buffs
			// are +4%, not +4.04%. Truncated, like every other client-side percentage read. A debuff can only take a
			// stat to 0, never negative.
			int Buffed(CharacterStatId stat)
			{
				var total = Own(stat);

				// A derived term adds a percentage of another stat. It reads that stat's own value rather than its
				// `Buffed` one, which is what keeps this non-recursive - and is right on every shipped row, whose
				// sources are all base attributes and so carry no derived term of their own. A cycle would otherwise
				// be expressible in data outside our control.
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
					// A delta that states conditions applies only when they hold. The carrier is both subject and
					// target here - a live buff's stat contribution is read off the entity wearing it, and the entity
					// that cast it is long gone by the time this recomputes.
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

			// The carrier's own sheet, as the evaluator wants it: the live buffed value where one exists, with level
			// and current health read off the entity itself.
			int? StatOf(CharacterStatId stat)
			{
				// Null, not 0, for a stat this entity cannot supply - the evaluator treats null as unevaluated and lets
				// the effect through. Reading an absent stat as 0 would drop every effect gated on `> n`.
				return stat switch
				{
					CharacterStatId.Level => (int)entity.Level,
					CharacterStatId.HealthCurrent => (int?)entity.Vitals?.Health,
					_ => entity.BuffedStats.TryGetValue(stat, out var value) ? (int)value : null
				};
			}
		}

		/// <summary>
		///     The scale a flat spell amount is stored at.
		/// </summary>
		/// <remarks>
		///     It is the stat's wire scale - the one item modifiers use - except for attributes, which spells store in
		///     display units: "Potion of Strength" carries 0x324 = 2, and the client's description for the sibling
		///     0x32E records reads "Augmente la Dextérité de 1/2", so a x10 read would make them +0.2 Strength. Items
		///     do store attributes x10, so the two sources differ here and this is not
		///     <see cref="CharacterStatId" />.GetScale().
		/// </remarks>
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

		/// <summary>
		///     The gate's answer for one imported row - honored, or dropped and by which half.
		/// </summary>
		/// <remarks>
		///     The two dropped-stat members stay apart rather than merged so the load summary can report them
		///     separately.
		/// </remarks>
		public enum Gate
		{
			Flat,
			Percent,
			UnhandledKind,
			UnmodelledStat,
			PercentOnUnmodelledStat,

			/// <summary>
			///     53189 with a source stat different from its target, and both ends resolvable - "raise stat B by N%
			///     of stat A".
			/// </summary>
			Derived,

			/// <summary>
			///     The same shape with an unresolvable source.
			/// </summary>
			/// <remarks>
			///     Its own member rather than folded into <see cref="UnhandledKind" />, because the kind is handled: it
			///     is this row's source that is not, and the load summary should say which of the two ends it dropped.
			/// </remarks>
			DerivedFromOtherStat
		}

		/// <summary>
		///     One honored delta a spell carries: which stat, and what it is worth.
		/// </summary>
		/// <remarks>
		///     Not a bare amount, because a delta is either a constant or a level ramp and only the carrier's level
		///     resolves the two at the point of use - see <see cref="Delta.At" />.
		/// </remarks>
		/// <param name="Override">
		///     The delta replaces the stat rather than adding to it, because the client's own executor for the kind is
		///     <c>SetStatOverride</c> - see <see cref="OverrideKinds" />.
		/// </param>
		internal readonly record struct Delta(CharacterStatId Stat, int Amount, LevelRamp? Ramp = null,
			IReadOnlyList<ItemTemplateStatModifiers.Criterion>? Criteria = null, bool Override = false,
			CharacterStatId? SourceStat = null)
		{
			/// <summary>
			///     The level band this delta states its numbers for, or null when it is level-independent.
			/// </summary>
			/// <remarks>
			///     The ramp's own two levels, because a record that bands a stat bands it by ramping each band
			///     separately.
			/// </remarks>
			public (uint Low, uint High)? Band => Ramp is { } ramp ? (ramp.LowLevel, ramp.HighLevel) : null;

			/// <summary>
			///     The delta's value for a carrier of this level, in the units the catalog stores for this kind:
			///     display units for a flat row (already divided at build time), raw hundredths for a percent one.
			/// </summary>
			/// <remarks>
			///     A ramp interpolates in the raw units it was decoded in and divides afterwards, which is why it is
			///     the one shape the build cannot pre-convert.
			/// </remarks>
			public int At(uint level)
			{
				return Ramp is { } ramp
					? ramp.ValueAt(level) / (int)SpellScale(Stat)
					: Amount;
			}
		}

		/// <summary>
		///     The honored deltas per spell base id - flat ones in display units, percent ones raw (x100 of a percent)
		///     - plus what the filter dropped and why.
		/// </summary>
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
