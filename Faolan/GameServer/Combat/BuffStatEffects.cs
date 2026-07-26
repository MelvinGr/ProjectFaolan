using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
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
	// fatality proc are their own id-keyed paths (PetSummonCatalog / MountCatalog / FatalityService / …). This owns one
	// question - "what does an active buff add to its carrier's stats" - and answers it from data.
	public sealed class BuffStatEffects
	{
		// The PERCENT kind, counted separately in the load summary because it is the biggest known gap: those buffs
		// scale a computed TOTAL (+2% Critical Rating), which needs a modifier model the server does not have yet.
		internal const uint PercentKind = 53067;

		// The kinds whose Amount is a FLAT addition (as opposed to a percent modifier or a health effect). Classified
		// empirically over all 1714 imported records: these carry values that read as flat in their own units against
		// the spell's rank ladder and the client's description text (Fend +1000 Armor / +500 Protection, Battle Cry
		// +62 Hit Rating, "Augmente la Dextérité de 1" for the 0x32E=1 record).
		private static readonly HashSet<uint> FlatKinds = [53045, 53189, 53220, 53221];

		// The stats a flat record is applied for, and where the live value lands. A stat is here only if its wire scale
		// is verified (see the type comment); the action is null for the ones the server does not model yet, which are
		// still pushed to the client so the character sheet reflects the buff.
		private static readonly Dictionary<CharacterStatId, Action<WorldEntity, uint>?> HonoredStats = new()
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

		// spell BASE id -> its honored deltas, in DISPLAY units. Loaded once (1.7k rows) on first use.
		private IReadOnlyDictionary<uint, (CharacterStatId Stat, int Amount)[]>? _catalog;

		public BuffStatEffects(IDatabaseRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// The honored deltas a buff carries, in display units. Empty for a spell with no region, or one whose records
		// are all percent / health / unmodelled-stat.
		public async Task<IReadOnlyList<(CharacterStatId Stat, int Amount)>> ForSpellBase(uint spellBase)
		{
			var catalog = await EnsureLoaded();
			return catalog.TryGetValue(spellBase, out var deltas)
				? deltas
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
			var honored = new Dictionary<uint, List<(CharacterStatId Stat, int Amount)>>();
			int percentSkipped = 0, kindSkipped = 0, statSkipped = 0;
			foreach (var row in rows)
			{
				if (!FlatKinds.Contains(row.EffectKind))
				{
					if (row.EffectKind == PercentKind)
						percentSkipped++;
					else
						kindSkipped++;
					continue;
				}

				if (!HonoredStats.ContainsKey(row.StatId))
				{
					statSkipped++;
					continue;
				}

				(honored.TryGetValue(row.SpellId, out var list)
					? list
					: honored[row.SpellId] = []).Add((row.StatId, row.Amount / (int)SpellScale(row.StatId)));
			}

			return new Catalog(honored.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), percentSkipped, kindSkipped, statSkipped);
		}

		// The recompute, as a pure function over an entity + a catalog: `gear + buffs` per honored stat, written to the
		// live fields, returning only what changed. Separate from Sync so it is testable without a database.
		internal static IReadOnlyList<(CharacterStatId Stat, uint Value)> Recompute(WorldEntity entity, IReadOnlyDictionary<uint, (CharacterStatId Stat, int Amount)[]> catalog)
		{
			// Sum the honored deltas of every live buff. The ActiveBuffs key is the WIRE id ((base << 8) | rank).
			var totals = new Dictionary<CharacterStatId, int>();
			foreach (var buffId in entity.ActiveBuffs.Keys)
			{
				if (!catalog.TryGetValue(buffId >> 8, out var deltas))
					continue;

				foreach (var (stat, amount) in deltas)
					totals[stat] = totals.GetValueOrDefault(stat) + amount;
			}

			var changed = new List<(CharacterStatId Stat, uint Value)>();
			foreach (var (stat, apply) in HonoredStats)
			{
				var buffed = entity.GearBonuses.GetValueOrDefault(stat) + totals.GetValueOrDefault(stat);
				var value = buffed > 0
					? (uint)buffed
					: 0u; // a debuff can only take a stat to 0, never negative

				// First sync seeds the baseline: nothing has changed if the stat is still what gear alone gives.
				if (entity.BuffedStats.TryGetValue(stat, out var previous)
					    ? previous == value
					    : totals.GetValueOrDefault(stat) == 0)
					continue;

				entity.BuffedStats[stat] = value;
				apply?.Invoke(entity, value);
				changed.Add((stat, value));
			}

			return changed;
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

		private async Task<IReadOnlyDictionary<uint, (CharacterStatId Stat, int Amount)[]>> EnsureLoaded()
		{
			if (_catalog != null)
				return _catalog;

			await _loadLock.WaitAsync();
			try
			{
				if (_catalog != null)
					return _catalog;

				var rows = await _database.GetSpellStatEffects();
				var catalog = BuildCatalog(rows);
				_catalog = catalog.BySpellBase;
				_logger.LogInformation(
					"BuffStatEffects: {Spells} spell(s) with applicable stat effects out of {Rows} imported records ({Percent} percent-kind, {Kind} other kinds, {Stat} unmodelled stats - see #367).",
					_catalog.Count, rows.Length, catalog.PercentSkipped, catalog.KindSkipped, catalog.StatSkipped);
				return _catalog;
			}
			finally
			{
				_loadLock.Release();
			}
		}

		// The honored deltas per spell base id, plus what the filter dropped and why.
		internal readonly record struct Catalog(
			IReadOnlyDictionary<uint, (CharacterStatId Stat, int Amount)[]> BySpellBase,
			int PercentSkipped,
			int KindSkipped,
			int StatSkipped);
	}
}
