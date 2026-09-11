using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The instant half of what a spell does: the health, mana and stamina it moves the moment it lands, as opposed
	///     to the lasting stat deltas <see cref="BuffStatEffects" /> owns. A heal is this; +62 Hit Rating for 20
	///     minutes is that.
	/// </summary>
	/// <remarks>
	///     Both come out of the same imported <c>SpellStatEffect</c> table and are told apart by the record's kind:
	///     the kind says instant against lasting (the kinds below, against <see cref="BuffStatEffects" />' flat and
	///     percent ones), the stat says which pool (27 health, 506 mana, 504 stamina), the sign says heal against
	///     damage, and the scale is the ordinary x100 vitals scale, which the client's own divisor table confirms for
	///     all three stat ids (<c>StatDisplayScale.Percent1</c> contains 0x01B, 0x1F8 and 0x1FA). 52 class-castable
	///     spells carry one - the Necromancer's whole <c>Lifestrike</c> line, <c>Aura of Infusion</c>,
	///     <c>Desecrating Essence</c>, <c>Adrenaline Surge</c> (mana), <c>Formation of Resolve</c> (stamina) - as does
	///     every health potion in the catalog.
	/// </remarks>
	public sealed class SpellPoolEffects
	{
		/// <summary>
		///     Builds the instant-pool-move catalog at startup rather than on the first heal or nuke - see
		///     <see cref="BuffStatEffects.WarmAsync" />.
		/// </summary>
		public async Task WarmAsync() => await EnsureLoaded();

		// The kinds whose Amount is an instant, flat move of a pool at the x100 wire scale. `Hit` and its variants are
		// one thing as far as a pool is concerned - the distinctions between them (magical, DPS-scaled, interpolated)
		// are about how retail computed the number, not about what to do with it.
		//
		// `MagicHit` (53256) is deliberately absent, and its absence is a decode rather than an omission: its key list
		// is `[39 Value, 71]` and carries no stat key at all, so it can never name a pool. Read positionally it looks
		// like a max-health drain on stat 1, but that "stat" is key 39's own value.
		private static readonly HashSet<uint> FlatKinds = [53002, 53013, 53219];

		// `HitPercentage` states its own unit through a carrier's name: the dev spell `System Spell: Fire AOE damage
		// 25% of Life` carries -25. This kind's Amount is therefore a plain percent of the pool rather than a value at
		// the stat's display scale, which is what separates it from the flat kinds without needing a fit.
		private const uint PercentKind = 53007;

		// The three current-pool stats. Deliberately not the max ones: a `HealthMax` row here would be a max-health
		// change rather than a heal, which is a different mechanic with its own clamping.
		private static readonly HashSet<CharacterStatId> Pools = [CharacterStatId.HealthCurrent, CharacterStatId.ManaCurrent, CharacterStatId.StaminaCurrent];

		// The drain sentinel. `Demonic Puppet` carries -100000000 on health, which is a marker rather than a number -
		// applied literally it would kill anything it touched. `BuffStatEffects` knows the same value.
		private const int DrainSentinel = 100_000_000;

		// The x100 wire scale the three pool stats are stored at.
		private const int VitalsScale = 100;

		private readonly ICatalogRepository _database;
		private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
		private readonly ILogger _logger;
		private IReadOnlyDictionary<uint, PoolEffect[]>? _catalog;

		public SpellPoolEffects(ICatalogRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		/// <summary>
		///     The instant pool effects a spell base carries for a caster of this level, or empty - which is the
		///     overwhelming majority.
		/// </summary>
		/// <remarks>
		///     The level is not decoration. A record states one element per level band (<c>Hellfire Stream</c>'s five,
		///     banded 1-5 through 21-30), so the set has to be resolved rather than applied whole - see
		///     <see cref="SpellEffectBands" /> - and the magnitude inside the chosen band is itself a ramp over the
		///     same two levels.
		/// </remarks>
		public async Task<IReadOnlyList<PoolEffect>> ForSpellBase(uint spellBase, uint level)
		{
			var catalog = await EnsureLoaded();
			if (!catalog.TryGetValue(spellBase, out var effects))
				return [];

			// Banded per pool: two effects on different pools are two mechanics, and neither is an alternative reading
			// of the other. Within one pool they are the same statement said once per band.
			var resolved = new List<PoolEffect>(effects.Length);
			foreach (var pool in effects.Select(e => e.Pool).Distinct())
				resolved.AddRange(SpellEffectBands.ForLevel([.. effects.Where(e => e.Pool == pool)], level, e => e.Band));

			return resolved;
		}

		/// <summary>
		///     Whether this imported row is an instant pool move this engine applies.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Public for the same reason <see cref="BuffStatEffects.Classify" /> is: the imported-versus-applied
		///         census (<c>--report spell-effect-gap</c>) has to ask both engines whether a row lands anywhere, and
		///         a census that copies the rule instead of calling it drifts away from it.
		///     </para>
		///     <para>
		///         <c>PeriodCentiseconds == 0</c> is load-bearing. The period is what separates two mechanics stated on
		///         one stat - <c>Daggamalt</c>'s 405 HP instantly against its 67.5 HP every 100 cs - and 726 shipped
		///         spells state both. Without it every one of them applies its ticks as extra instant magnitude on top
		///         of the instant row. The ticking half belongs to the DoT engine (<c>Spell.DotDelta</c> /
		///         <c>DotIntervalMs</c>), which reads its own columns off the spell row.
		///     </para>
		/// </remarks>
		public static bool Applies(SpellStatEffect row)
		{
			return (row.EffectKind == PercentKind || FlatKinds.Contains(row.EffectKind))
			       && Pools.Contains(row.StatId)
			       && row.PeriodCentiseconds == 0
			       && row.Amount != 0
			       && Math.Abs(row.Amount) < DrainSentinel;
		}

		/// <summary>
		///     The catalog build, as a pure function over the imported rows, so it is testable without a database.
		/// </summary>
		internal static Dictionary<uint, PoolEffect[]> BuildCatalog(IEnumerable<SpellStatEffect> rows)
		{
			var bySpell = new Dictionary<uint, List<PoolEffect>>();
			// `DistinctBy(Statement)`: a record can state the same element twice (`Hellfire Stream` repeats two of its
			// five bands verbatim), and a repeated statement is one statement - see `SpellStatEffect.Statement`.
			foreach (var row in rows.DistinctBy(r => r.Statement))
			{
				if (!Applies(row))
					continue;

				// The endpoints are carried raw and divided after the interpolation, the same order `BuffStatEffects`
				// uses and for the same reason: the lerp is the client's float arithmetic over the stored numbers, so
				// dividing first rounds twice and the two disagree by one over most of a ramp's span.
				var ramp = row is { HighAmount: { } high, LowLevel: { } low, HighLevel: { } top }
					? new LevelRamp(row.Amount, high, low, top)
					: (LevelRamp?)null;

				// The range's upper endpoint and its own ramp. A damaging spell states a min and a max - `Ice Strike
				// (Rank 6)` is -16300..-18950, the 163-189.5 the client's own tooltip renders - and picking one end
				// silently would make every cast a floor or a ceiling.
				var maxRamp = row is { MaxAmount: { } maxLow, MaxHighAmount: { } maxHigh, LowLevel: { } mlow, HighLevel: { } mtop }
					? new LevelRamp(maxLow, maxHigh, mlow, mtop)
					: (LevelRamp?)null;

				(bySpell.TryGetValue(row.SpellId, out var list) ? list : bySpell[row.SpellId] = [])
					.Add(new PoolEffect(row.StatId, row.Amount, ramp, row.EffectKind == PercentKind, row.MaxAmount, maxRamp));
			}

			return bySpell.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
		}

		private async Task<IReadOnlyDictionary<uint, PoolEffect[]>> EnsureLoaded()
		{
			if (_catalog != null)
				return _catalog;

			await _loadLock.WaitAsync();
			try
			{
				if (_catalog != null)
					return _catalog;

				var clock = System.Diagnostics.Stopwatch.StartNew();
				var rows = await _database.GetSpellStatEffects();
				var catalog = BuildCatalog(rows);
				_catalog = catalog;
				_logger.LogInformation("SpellPoolEffects: {Spells} spell(s) move a pool instantly, out of {Rows} imported records (#702). Built in {Ms} ms.", catalog.Count, rows.Length, clock.ElapsedMilliseconds);

				return catalog;
			}
			finally
			{
				_loadLock.Release();
			}
		}

		/// <summary>
		///     One instant pool move: which pool, and by how much for a carrier of that level with that
		///     <paramref name="max" />.
		/// </summary>
		/// <remarks>
		///     Negative is damage and positive is a heal, in the pool's own display units, so the caller hands the
		///     result straight to <c>EntityVitals</c>, which clamps it to <c>[0, max]</c>.
		///     <para>
		///         <paramref name="Ramp" /> is non-null on the interpolated kinds, whose <paramref name="Amount" /> is
		///         only the value at the ramp's low level. <c>InterpolatedHit</c> (53219) is the whole of the
		///         instant-heal family, so a null ramp there would give a level-80 caster the level-1 number.
		///     </para>
		/// </remarks>
		public readonly record struct PoolEffect(CharacterStatId Pool, int Amount, LevelRamp? Ramp, bool IsPercent,
			int? MaxAmount = null, LevelRamp? MaxRamp = null)
		{
			// The level band this effect states its numbers for, or null when it is level-independent. It is the ramp's
			// own two levels - a record that bands its magnitudes bands them by ramping each band separately.
			public (uint Low, uint High)? Band => Ramp is { } ramp ? (ramp.LowLevel, ramp.HighLevel) : null;

			public int For(uint max, uint level)
			{
				return Resolve(Ramp?.ValueAt(level) ?? Amount, max);
			}

			/// <summary>
			///     The move at the upper end of the effect's stated range, or <see cref="For" /> where it states one
			///     number.
			/// </summary>
			public int MaxFor(uint max, uint level)
			{
				return MaxAmount is { } top ? Resolve(MaxRamp?.ValueAt(level) ?? top, max) : For(max, level);
			}

			/// <summary>
			///     A point in the effect's stated range: <paramref name="rollPercent" /> 0 is its low end, 100 its
			///     high one, interpolated linearly the way <c>CombatResolver.ResolveMeleeDamage</c> rolls a weapon.
			/// </summary>
			public int Rolled(uint max, uint level, int rollPercent)
			{
				var low = For(max, level);
				var high = MaxFor(max, level);

				return low + (high - low) * Math.Clamp(rollPercent, 0, 100) / 100;
			}

			private int Resolve(int amount, uint max)
			{
				return IsPercent
					? (int)((long)max * amount / 100)
					: amount / VitalsScale;
			}
		}
	}
}
