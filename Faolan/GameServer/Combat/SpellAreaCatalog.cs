using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The <c>SpellAreaEffects</c> catalog, held in memory: which spells a spell runs, how, and on what period.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Read whole and cached because every caller sits on a hot path - the cast resolve and the world tick -
	///         and the table is a few thousand rows that only the importer writes. Same shape as
	///         <see cref="SpellEffectService" />'s cache, for the same reason.
	///     </para>
	///     <para>
	///         The first split is the row's <c>Kind</c>, and it comes before the period, because the table holds two
	///         kinds of link and only one targets an area. An area kind applies the effect to everyone in a radius,
	///         and <c>PeriodCentiseconds</c> then says whether that happens once or on a cadence.
	///         <c>CastNano</c> (53051) means "also run X" on the same subject, with no radius at all; read as an area
	///         it would sweep a 0 m circle, which is a chain edge that looks applied and reaches nobody.
	///     </para>
	///     <para>
	///         Within the area kinds, <c>PeriodCentiseconds</c> is the split the two callers need: 0 is applied once
	///         by the cast (<c>Life Leech</c>: 220980 to 12 targets within 5 m, and that is all of it), while a
	///         positive period is an aura re-applied by the tick every period while the buff that owns it is up
	///         (<c>Gangrenous Stench</c>: 221069 to 12 targets within 7 m, every 4 s, for the 25 s the self-buff
	///         lasts).
	///     </para>
	///     <para>
	///         Not sealed, and the lookups the world tick uses are virtual, because the simulation reads this catalog
	///         directly: a tick test that wants to drive a specific chain shape subclasses this rather than stubbing a
	///         wire port - see <c>RecordingSpellAreaCatalog</c> in Faolan.Tests.
	///     </para>
	/// </remarks>
	public class SpellAreaCatalog
	{
		private readonly ICatalogRepository _database;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		private Dictionary<uint, SpellAreaEffect[]>? _bySpell;

		// Spell id -> its `GuiStackingLine`, cached the same way and for the same reason. The chain walk needs it per
		// edge, and a stacking line is a mutual-exclusion group, so this is what stops one walk applying ten members of
		// one line.
		private Dictionary<uint, uint>? _lineOf;

		// (spell, link sequence) -> the link's condition run, in wire order. Cached with the links themselves because
		// the chain walk needs it per edge on the same hot path.
		private Dictionary<(uint SpellId, uint Sequence), ItemTemplateStatModifiers.Criterion[]>? _criteria;

		public SpellAreaCatalog(ICatalogRepository database)
		{
			_database = database;
		}

		/// <summary>The areas applied once when <paramref name="spellId" /> is cast.</summary>
		public async Task<IReadOnlyList<SpellAreaEffect>> InstantAreas(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => IsArea(a) && a.PeriodCentiseconds == 0).ToList();
		}

		/// <summary>The areas re-applied on a period while <paramref name="spellId" />'s buff is up.</summary>
		public virtual async Task<IReadOnlyList<SpellAreaEffect>> AuraAreas(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => IsArea(a) && a.PeriodCentiseconds > 0).ToList();
		}

		/// <summary>
		///     The spells <paramref name="spellId" /> also runs - its <c>CastNano</c> chain edges.
		/// </summary>
		/// <remarks>
		///     Not an area: these land on whatever the carrier landed on, which is why they are a separate accessor
		///     rather than a radius-0 member of the two above.
		/// </remarks>
		public virtual async Task<IReadOnlyList<SpellAreaEffect>> ChainedSpells(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => !IsArea(a)).ToList();
		}

		/// <summary>
		///     The chain edges that re-run on a cadence while <paramref name="spellId" />'s buff is up - the tick's
		///     half of <see cref="ChainedSpells" />.
		/// </summary>
		/// <remarks>
		///     <para>
		///         <c>Repeats</c> is what splits them, and without it an interval cannot be read at all. The client
		///         names both words in one log line - <c>"Fixing broken gameaction data, Duration was 0 while Interval
		///         was N"</c> - and its executor schedules an element rather than running it inline when
		///         <c>interval &gt; 0 || duration &gt; 1</c>.
		///     </para>
		///     <para>
		///         Of the 483 chain edges that carry an interval, 263 state <c>Repeats == -1</c> and belong here; the
		///         other 220 state <c>Repeats == 1</c>, so they run once and their interval is a delay. That half needs
		///         a one-shot timer and is not wired. Open work tracked in Gitea #786.
		///     </para>
		/// </remarks>
		public virtual async Task<IReadOnlyList<SpellAreaEffect>> RepeatingChains(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => !IsArea(a) && a.PeriodCentiseconds > 0 && a.Repeats == UnboundedRepeats).ToList();
		}

		/// <summary>
		///     The <c>Repeats</c> value meaning "for as long as the carrier lasts" - the element's 0x2d5 read signed.
		/// </summary>
		private const int UnboundedRepeats = -1;

		// Whether this link targets an area, which is a property of the GameAction that stated it rather than of its
		// radius. Asking the radius instead would file a `CastNano` as a degenerate area, and an area element that
		// happens to state 0 as a chain edge.
		private static bool IsArea(SpellAreaEffect link)
		{
			return GameActionTypes.AreaTargetKinds.Contains(link.Kind);
		}

		/// <summary>
		///     The <c>GuiStackingLine</c> `spellId` belongs to, or 0 when it is in none.
		/// </summary>
		/// <remarks>
		///     A stacking line is a mutual-exclusion group, which is what makes it the right key for a chain walk: two
		///     members of one line is something the client cannot represent - applying line 44235 wholesale puts all
		///     ten ranks of Furious Inspiration on the bar at once.
		/// </remarks>
		public virtual async Task<uint> StackingLineOf(uint spellId)
		{
			if (_lineOf == null)
			{
				await _lock.WaitAsync();
				try
				{
					// Last-wins on a duplicate id, which cannot happen (Spells.Id is the key) but costs nothing to
					// state - `ToDictionary` would throw where this simply picks.
					_lineOf ??= BuildLineMap(await _database.GetSpellLineMembers());
				}
				finally
				{
					_lock.Release();
				}
			}

			return _lineOf.GetValueOrDefault(spellId);
		}

		private static Dictionary<uint, uint> BuildLineMap((uint Line, uint SpellId)[] members)
		{
			var map = new Dictionary<uint, uint>(members.Length);
			foreach (var (line, spellId) in members)
				map[spellId] = line;

			return map;
		}

		/// <summary>
		///     The conditions on one link, in wire order - empty when it carries none, which is most links.
		/// </summary>
		/// <remarks>
		///     On a rank table this run is the selection. Each edge is gated on
		///     <c>MustHavePassiveAbilityRunning &lt;the rank below it&gt;</c> and the edges are stored
		///     highest-rank-first, so evaluating them in order and taking the first that passes is a one-rank-per-proc
		///     upgrade: nothing running gives rank 1, rank 5 running gives rank 6, and the top edge ORs in itself so a
		///     character at the cap refreshes rather than falling off it.
		/// </remarks>
		public async Task<IReadOnlyList<ItemTemplateStatModifiers.Criterion>> CriteriaFor(SpellAreaEffect link)
		{
			if (_criteria == null)
			{
				await _lock.WaitAsync();
				try
				{
					_criteria ??= (await _database.GetSpellAreaEffectCriteria())
						.GroupBy(c => (c.SpellId, c.Sequence))
						.ToDictionary(g => g.Key, g => g.OrderBy(c => c.Pos)
							.Select(c => new ItemTemplateStatModifiers.Criterion(c.CriterionId, c.Value, c.Operator)).ToArray());
				}
				finally
				{
					_lock.Release();
				}
			}

			return _criteria.GetValueOrDefault((link.SpellId, link.Sequence), []);
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
