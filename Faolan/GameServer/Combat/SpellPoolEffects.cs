using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The INSTANT half of what a spell does (#702): the health, mana and stamina it moves the moment it lands, as
	// opposed to the lasting stat deltas `BuffStatEffects` owns. A heal is this; +62 Hit Rating for 20 minutes is
	// that. They come out of the same imported `SpellStatEffect` table and are told apart by the record's KIND.
	//
	// Nothing applied these before, which meant HEALING did not exist: 52 class-castable spells carry one - the
	// Necromancer's whole `Lifestrike` line, `Aura of Infusion`, `Desecrating Essence`, `Adrenaline Surge` (mana),
	// `Formation of Resolve` (stamina) - and every health potion in the catalog. All of them showed an icon and
	// moved no bar.
	//
	// Everything the effect needs was already imported and named; only the application was missing:
	//   * the KIND says instant-vs-lasting (the five below, against `BuffStatEffects`' flat/percent kinds);
	//   * the STAT says which pool - 27 health, 506 mana, 504 stamina;
	//   * the SIGN says heal vs damage;
	//   * the SCALE is the ordinary ×100 vitals scale, which the client's own divisor table confirms for all three
	//     stat ids (`StatDisplayScale.Percent1` contains 0x01B, 0x1F8 and 0x1FA).
	public sealed class SpellPoolEffects
	{
		// The kinds whose Amount is an instant, FLAT move of a pool at the ×100 wire scale. `Hit` and its variants
		// are all one thing as far as a pool is concerned - the distinctions between them (magical, DPS-scaled,
		// interpolated) are about how retail COMPUTED the number, not about what to do with it.
		//
		// ⚠️ `MagicHit` (53256) is NOT here, and its absence is a decode rather than an omission: its key list is
		// `[39 Value, 71]` and carries no stat key at all, so it can never name a pool. It used to appear in this
		// table under stat 1 `HealthMax` with the `Venomstrike`/`Banestrike` rank ladders, which is what made it look
		// like a max-health drain - that "stat" was key 39's own value, read positionally (#701).
		private static readonly HashSet<uint> FlatKinds = [53002, 53013, 53219];

		// ⭐ `HitPercentage` states its own unit through a carrier's NAME: the dev spell
		// `System Spell: Fire AOE damage 25% of Life` carries -25. So this kind's Amount is a plain percent OF THE
		// POOL, not a value at the stat's display scale - which is what separates it from the flat kinds without
		// needing a fit.
		private const uint PercentKind = 53007;

		// The three CURRENT-pool stats. Deliberately not the MAX ones: a `HealthMax` row here would be a max-health
		// change rather than a heal, which is a different mechanic with its own clamping.
		private static readonly HashSet<CharacterStatId> Pools = [CharacterStatId.HealthCurrent, CharacterStatId.ManaCurrent, CharacterStatId.StaminaCurrent];

		// ⚠️ The DRAIN SENTINEL. `Demonic Puppet` carries -100000000 on health, which is a marker rather than a
		// number - applied literally it would kill anything it touched. `BuffStatEffects` already knows it; so does
		// this.
		private const int DrainSentinel = 100_000_000;

		// The ×100 wire scale the three pool stats are stored at.
		private const int VitalsScale = 100;

		private readonly IDatabaseRepository _database;
		private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
		private readonly ILogger _logger;
		private IReadOnlyDictionary<uint, PoolEffect[]>? _catalog;

		public SpellPoolEffects(IDatabaseRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// The instant pool effects a spell BASE carries, or empty - which is the overwhelming majority.
		public async Task<IReadOnlyList<PoolEffect>> ForSpellBase(uint spellBase)
		{
			var catalog = await EnsureLoaded();
			return catalog.TryGetValue(spellBase, out var effects) ? effects : [];
		}

		// Whether this imported row is an instant pool move this engine applies. Public for the same reason
		// `BuffStatEffects.Classify` is: the imported-vs-applied census (`--report spell-effect-gap`) has to ask both
		// engines whether a row lands anywhere, and a census that copies the rule instead of calling it drifts away
		// from it - which is exactly how #701's count came to be four times the real one.
		public static bool Applies(SpellStatEffect row)
		{
			return (row.EffectKind == PercentKind || FlatKinds.Contains(row.EffectKind))
			       && Pools.Contains(row.StatId)
			       && row.Amount != 0
			       && Math.Abs(row.Amount) < DrainSentinel;
		}

		// The catalog build, as a pure function over the imported rows, so it is testable without a database.
		internal static Dictionary<uint, PoolEffect[]> BuildCatalog(IEnumerable<SpellStatEffect> rows)
		{
			var bySpell = new Dictionary<uint, List<PoolEffect>>();
			foreach (var row in rows)
			{
				if (!Applies(row))
					continue;

				(bySpell.TryGetValue(row.SpellId, out var list) ? list : bySpell[row.SpellId] = [])
					.Add(new PoolEffect(row.StatId, row.Amount, row.EffectKind == PercentKind));
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

				var rows = await _database.GetSpellStatEffects();
				var catalog = BuildCatalog(rows);
				_catalog = catalog;
				_logger.LogInformation("SpellPoolEffects: {Spells} spell(s) move a pool instantly, out of {Rows} imported records (#702).", catalog.Count, rows.Length);

				return catalog;
			}
			finally
			{
				_loadLock.Release();
			}
		}

		/// <summary>
		///     One instant pool move: which pool, and by how much for a carrier with that <paramref name="max" />.
		/// </summary>
		/// <remarks>
		///     Negative is damage and positive is a heal, in the pool's own DISPLAY units - so the caller hands the
		///     result straight to <c>EntityVitals</c>, which clamps it to <c>[0, max]</c>.
		/// </remarks>
		public readonly record struct PoolEffect(CharacterStatId Pool, int Amount, bool IsPercent)
		{
			public int For(uint max)
			{
				return IsPercent
					? (int)((long)max * Amount / 100)
					: Amount / VitalsScale;
			}
		}
	}
}
