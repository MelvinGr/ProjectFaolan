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
		private readonly IDatabaseRepository _database;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

		// carrier spell -> its modifier rows, and line -> member spells, both resolved once.
		private Dictionary<uint, SpellSpellStatModifier[]>? _byCarrier;
		private Dictionary<uint, uint[]>? _lineMembers;

		public SpellModifierService(IDatabaseRepository database)
		{
			_database = database;
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
			foreach (var row in await _database.GetCharacterFeatSpellStatModifiers(characterId))
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
				_byCarrier ??= (await _database.GetSpellStatModifiers())
					.GroupBy(m => m.SpellId)
					.ToDictionary(g => g.Key, g => g.OrderBy(m => m.Sequence).ToArray());

				_lineMembers ??= (await _database.GetSpellLineMembers())
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
		///     Applies only the RELATIVE half to a base value, clamped at zero.
		/// </summary>
		/// <remarks>
		///     ⚠️ The absolute half is deliberately NOT applied by this helper, and that is a statement about the
		///     evidence rather than a simplification. A percentage needs no unit: -1000 is -10% of whatever the base
		///     is, and the client's tooltip builder pins that scale (it prints the slot as <c>abs(amount) * 0.01</c>
		///     under a <c>"%.1f%%"</c> format). An ABSOLUTE delta is in "the stat's own wire units", and for the stats
		///     a caller would want it on - <c>Casting Time (Seconds)</c>, <c>Spell Critical Chance</c> - nothing has
		///     pinned what those units are against the columns we hold. Applying one would be inventing a scale.
		///     <para>
		///         So callers use this where the modifier is overwhelmingly relative anyway - <c>Mana Cost</c> is 217
		///         percent rows against 11 absolute - and the absolute half stays readable through
		///         <see cref="Absolute" /> for whoever pins a unit. Open as #787.
		///     </para>
		/// </remarks>
		public int ApplyPercent(int baseValue, CharacterStatId stat)
		{
			var pct = PercentHundredths(stat);
			if (pct == 0 || baseValue == 0)
				return baseValue;

			var scaled = baseValue * (10000L + pct) / 10000L;
			return scaled <= 0 ? 0 : (int)scaled;
		}
	}
}
