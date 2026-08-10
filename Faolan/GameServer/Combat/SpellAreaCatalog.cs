using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;

namespace Faolan.GameServer.Combat
{
	// The `SpellAreaEffects` catalog, held in memory: "which spells does spell X run, how, and on what period"
	// (#772/#783).
	//
	// Read whole and cached because every caller sits on a hot path - the cast resolve and the world tick - and the
	// table is a few thousand rows that only the importer writes. Same shape as `SpellEffectService`'s cache, for the
	// same reason.
	//
	// ⚠️ The FIRST split is the row's `Kind`, and it comes before the period. The table holds two kinds of link, and
	// only one of them targets an area:
	//   * an AREA kind - applies the effect to everyone in a radius, and `PeriodCentiseconds` then says whether that
	//     happens once or on a cadence;
	//   * `CastNano` (53051) - "also run X" on the SAME subject, with no radius at all (#783). Read as an area it
	//     would sweep a 0 m circle, which is silently nothing - a chain edge that looks applied and reaches nobody.
	//
	// Within the area kinds the split the two callers need is `PeriodCentiseconds`:
	//   * 0  - applied ONCE, by the cast (`Life Leech`: 220980 to 12 targets within 5 m, and that is all of it);
	//   * >0 - an AURA, re-applied by the tick every period while the buff that owns it is up (`Gangrenous Stench`:
	//          221069 to 12 targets within 7 m, every 4 s, for the 25 s the self-buff lasts).
	public sealed class SpellAreaCatalog
	{
		private readonly IDatabaseRepository _database;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		private Dictionary<uint, SpellAreaEffect[]>? _bySpell;

		public SpellAreaCatalog(IDatabaseRepository database)
		{
			_database = database;
		}

		// The areas applied ONCE when `spellId` is cast.
		public async Task<IReadOnlyList<SpellAreaEffect>> InstantAreas(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => IsArea(a) && a.PeriodCentiseconds == 0).ToList();
		}

		// The areas re-applied on a period while `spellId`'s buff is up.
		public async Task<IReadOnlyList<SpellAreaEffect>> AuraAreas(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => IsArea(a) && a.PeriodCentiseconds > 0).ToList();
		}

		// The spells `spellId` also RUNS - its `CastNano` chain edges (#783). Not an area: these land on whatever the
		// carrier landed on, which is why they are a separate accessor rather than a radius-0 member of the two above.
		public async Task<IReadOnlyList<SpellAreaEffect>> ChainedSpells(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => !IsArea(a)).ToList();
		}

		/// <summary>
		///     The chain edges that re-run on a CADENCE while `spellId`'s buff is up (#786) - the tick's half of
		///     <see cref="ChainedSpells" />.
		/// </summary>
		/// <remarks>
		///     <para>
		///         ⭐ <c>Repeats</c> is what splits them, and without it an interval cannot be read at all. The client
		///         names both words in one log line - <c>"Fixing broken gameaction data, Duration was 0 while Interval
		///         was N"</c> - and its executor schedules an element rather than running it inline when
		///         <c>interval &gt; 0 || duration &gt; 1</c>.
		///     </para>
		///     <para>
		///         Of the 483 chain edges that carry an interval, <b>263 state <c>Repeats == -1</c></b> and belong
		///         here; the other 220 state <c>Repeats == 1</c>, so they run ONCE and their interval is a DELAY -
		///         which the cast path already does, minus the wait. That half needs a one-shot timer and is not
		///         wired.
		///     </para>
		/// </remarks>
		public async Task<IReadOnlyList<SpellAreaEffect>> RepeatingChains(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => !IsArea(a) && a.PeriodCentiseconds > 0 && a.Repeats == UnboundedRepeats).ToList();
		}

		// The `Repeats` value that means "for as long as the carrier lasts" - the element's 0x2d5 read signed.
		private const int UnboundedRepeats = -1;

		// Whether this link targets an AREA - which is a property of the GameAction that stated it, not of its radius.
		// Asking the radius instead would file a `CastNano` as a degenerate area and an area element that happens to
		// state 0 as a chain edge, and both readings are wrong for the same reason.
		private static bool IsArea(SpellAreaEffect link)
		{
			return GameActionTypes.AreaTargetKinds.Contains(link.Kind);
		}

		private async Task<SpellAreaEffect[]> ForSpell(uint spellId)
		{
			if (_bySpell == null)
			{
				await _lock.WaitAsync();
				try
				{
					_bySpell ??= (await _database.GetSpellAreaEffects())
						.GroupBy(a => a.SpellId)
						.ToDictionary(g => g.Key, g => g.OrderBy(a => a.Sequence).ToArray());
				}
				finally
				{
					_lock.Release();
				}
			}

			return _bySpell.GetValueOrDefault(spellId, []);
		}
	}
}
