using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Combat
{
	// "What does this caster's Fireball actually cost, and how long does it take?" (#710) - the per-cast overrides a
	// character's TRAINED FEATS and LIVE BUFFS put on a spell's own properties.
	//
	// ⭐ ONE MECHANISM, TWO SOURCES. The client states the identical record - GameAction kinds 53054
	// `SpellStatModifier` and 53062 `SpellLineStatModifier`, the same four slots - on a FEAT
	// (`FeatSpellStatModifier`, #593) and on a SPELL (`SpellSpellStatModifier`, #710). A feat that makes your Shock
	// cheaper and a buff that makes your next Shock cheaper differ only in where the row was read from and how it
	// reaches the caster, so they are folded here rather than in two engines.
	//
	// ⚠️ The catalog half is read WHOLE and cached (1,571 rows the importer owns), the same shape and for the same
	// reason as `SpellAreaCatalog`: the lookup is on the cast path. The FEAT half is per character and is not cached
	// here - training changes it, and it is one indexed query.
	public sealed class SpellModifierService
	{
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterStatRepository _stats;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

		// carrier spell -> its modifier rows, and line -> member spells, both resolved once.
		private Dictionary<uint, SpellSpellStatModifier[]>? _byCarrier;
		private Dictionary<uint, uint[]>? _lineMembers;

		public SpellModifierService(ICatalogRepository catalog, ICharacterStatRepository stats)
		{
			_catalog = catalog;
			_stats = stats;
		}

		// The effective modifiers on `spellId` for this caster: their trained feats, plus every live buff whose spell
		// states a modifier aimed at it. `activeBuffWireIds` are the WIRE ids the buff registry is keyed by
		// ((base << 8) | rank); the catalog is keyed by the base.
		public async Task<SpellModifiers> For(uint characterId, IEnumerable<uint> activeBuffWireIds, uint spellId)
		{
			await EnsureCatalog();

			var absolute = new Dictionary<CharacterStatId, int>();
			var percent = new Dictionary<CharacterStatId, int>();

			// The FEAT half. A row is kept only if it aims at THIS spell - directly, or through a line this spell is
			// a rank of.
			foreach (var row in await _stats.GetCharacterFeatSpellStatModifiers(characterId))
				if (Targets(row.SpellId, row.SpellLineId, spellId))
					Fold(absolute, percent, row.StatId, row.Value, row.PercentHundredths);

			// The BUFF half, off the same catalog every cast reads.
			foreach (var wireId in activeBuffWireIds)
			{
				if (!_byCarrier!.TryGetValue(wireId >> 8, out var rows))
					continue;

				foreach (var row in rows)
					if (Targets(row.TargetSpellId, row.TargetSpellLineId, spellId))
						Fold(absolute, percent, row.StatId, row.Value, row.PercentHundredths);
			}

			return new SpellModifiers(absolute, percent);
		}

		// Convenience for the common caller shape - a live player entity carries its own buff registry.
		public Task<SpellModifiers> For(uint characterId, WorldEntity? entity, uint spellId)
		{
			return For(characterId, entity?.ActiveBuffs.Keys ?? [], spellId);
		}

		// Whether a modifier row aims at `spellId`. Exactly one of the two target columns is set - the kind says
		// which - and a LINE row covers every rank of the line at once, which is the whole point of the 53062 form.
		private bool Targets(uint? targetSpellId, uint? targetLineId, uint spellId)
		{
			if (targetSpellId is { } direct)
				return direct == spellId;

			return targetLineId is { } line && _lineMembers!.TryGetValue(line, out var members) && members.Contains(spellId);
		}

		// ⚠️ `Value` and `PercentHundredths` are ALTERNATIVES, never a pair - the client's own tooltip builder renders
		// them through two different formatters, and exactly one is set on every shipped row. So they accumulate into
		// two separate totals rather than one number, and the caller decides how to combine them with the base.
		private static void Fold(Dictionary<CharacterStatId, int> absolute, Dictionary<CharacterStatId, int> percent,
			CharacterStatId stat, int? value, int? percentHundredths)
		{
			if (value is { } v)
				absolute[stat] = absolute.GetValueOrDefault(stat) + v;

			if (percentHundredths is { } p)
				percent[stat] = percent.GetValueOrDefault(stat) + p;
		}

		private async Task EnsureCatalog()
		{
			if (_byCarrier != null && _lineMembers != null)
				return;

			await _lock.WaitAsync();
			try
			{
				_byCarrier ??= (await _catalog.GetSpellStatModifiers())
					.GroupBy(m => m.SpellId)
					.ToDictionary(g => g.Key, g => g.OrderBy(m => m.Sequence).ToArray());

				_lineMembers ??= (await _catalog.GetSpellLineMembers())
					.GroupBy(x => x.Line)
					.ToDictionary(g => g.Key, g => g.Select(x => x.SpellId).ToArray());
			}
			finally
			{
				_lock.Release();
			}
		}
	}

	// The folded modifiers on one spell for one caster: the absolute deltas and the relative ones, kept apart because
	// the wire keeps them apart.
	public sealed class SpellModifiers
	{
		public static readonly SpellModifiers None = new SpellModifiers(new Dictionary<CharacterStatId, int>(), new Dictionary<CharacterStatId, int>());

		private readonly IReadOnlyDictionary<CharacterStatId, int> _absolute;
		private readonly IReadOnlyDictionary<CharacterStatId, int> _percent;

		internal SpellModifiers(IReadOnlyDictionary<CharacterStatId, int> absolute, IReadOnlyDictionary<CharacterStatId, int> percent)
		{
			_absolute = absolute;
			_percent = percent;
		}

		public bool IsEmpty => _absolute.Count == 0 && _percent.Count == 0;

		// The RELATIVE delta on a stat, in hundredths of a percent (-10000 = -100%), or 0 for none.
		public int PercentHundredths(CharacterStatId stat)
		{
			return _percent.GetValueOrDefault(stat);
		}

		// The ABSOLUTE delta on a stat, in the stat's own wire units, or 0 for none.
		public int Absolute(CharacterStatId stat)
		{
			return _absolute.GetValueOrDefault(stat);
		}

		/// <summary>
		///     ⭐ Applies BOTH halves to a base value held in the COLUMN's units, clamped at zero (#787).
		/// </summary>
		/// <remarks>
		///     <para>
		///         The absolute half was unapplied because "the stat's own wire units" was unpinned. It is pinned now,
		///         and by two independent sources that agree on every stat: the client's own divisor table
		///         (<see cref="StatDisplayScale" />, transcribed from <c>FUN_011197f0</c>/<c>FUN_01119ca0</c>) and the
		///         importer's existing field→column conversion. A modifier states its delta in the PROPERTY-RUN
		///         FIELD's units - the same units the column was built from - so the factor is whatever the importer
		///         already applies:
		///     </para>
		///     <list type="table">
		///         <item><c>294 Casting Time</c> → <c>CastTime</c> is <c>field * 10</c> ms, so the modifier is
		///         CENTISECONDS: the shipped -900..100 is -9.00 s..+1.00 s, and the client divides the same field by
		///         100 with no unit suffix.</item>
		///         <item><c>8 Duration</c> → <c>DurationMs</c> is <c>field * 10</c>, same reading.</item>
		///         <item><c>129</c>/<c>147</c> → the columns store the field RAW as hundredths of a percent and the
		///         client draws <c>field / 100</c> with a <c>%</c>, so a modifier of 2750 is +27.50% and needs no
		///         conversion at all.</item>
		///         <item><c>347</c>/<c>348</c> → <c>SplashRadius</c>/<c>SplashDamage</c> are <c>field / 100f</c>;
		///         <c>349</c> and <c>132</c> are stored raw, and <c>349</c> is the checkable one - the client scales
		///         it by 1, and every shipped modifier really is a small integer (1..20 targets).</item>
		///     </list>
		///     <para>
		///         ⚠️ ORDER: additive first, then relative. That is the client's own composition for a value carrying
		///         both - <c>FUN_014a9c10</c> forms <c>value + additive</c> and hands it to <c>FUN_014a9f90</c>, which
		///         multiplies (#782). Assumed to hold for spell-property modifiers, which are a different applier;
		///         it only matters where a stat carries both halves at once, which no shipped carrier does.
		///     </para>
		/// </remarks>
		public int Apply(int baseValue, CharacterStatId stat)
		{
			// ⚠️ Clamped HERE and not left to `ApplyPercent`, which only clamps when there is a percent to apply. A
			// -5000 absolute on a 1000 base has no relative half to pass through, and an unclamped sum reaches the
			// caller as a NEGATIVE cast time or mana cost.
			var shifted = baseValue + Absolute(stat) * ColumnScale(stat);
			return shifted <= 0 ? 0 : ApplyPercent(shifted, stat);
		}

		/// <summary>
		///     Applies only the RELATIVE half to a base value, clamped at zero. A percentage needs no unit, so this is
		///     the half that was always safe; <see cref="Apply" /> is what callers want.
		/// </summary>
		public int ApplyPercent(int baseValue, CharacterStatId stat)
		{
			var pct = PercentHundredths(stat);
			if (pct == 0 || baseValue == 0)
				return baseValue;

			var scaled = baseValue * (10000L + pct) / 10000L;
			return scaled <= 0 ? 0 : (int)scaled;
		}

		// The factor from a modifier's own units (the property-run field's) to the units the COLUMN holds. 1 wherever
		// the importer stores the field verbatim, which is all of them but the two centisecond→millisecond pairs.
		private static int ColumnScale(CharacterStatId stat)
		{
			return stat switch
			{
				CharacterStatId.CastingTimeSeconds => 10,
				CharacterStatId.Duration => 10,
				_ => 1
			};
		}
	}
}
