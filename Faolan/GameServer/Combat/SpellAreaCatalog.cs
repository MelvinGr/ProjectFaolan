using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Combat
{
	// The `SpellAreaEffects` catalog, held in memory: "which areas does spell X apply, and on what period" (#772).
	//
	// Read whole and cached because both callers sit on hot paths - the cast resolve and the world tick - and the
	// table is a few thousand rows that only the importer writes. Same shape as `SpellEffectService`'s cache, for the
	// same reason.
	//
	// The split the two callers need is `PeriodCentiseconds`:
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
			return (await ForSpell(spellId)).Where(a => a.PeriodCentiseconds == 0).ToList();
		}

		// The areas re-applied on a period while `spellId`'s buff is up.
		public async Task<IReadOnlyList<SpellAreaEffect>> AuraAreas(uint spellId)
		{
			return (await ForSpell(spellId)).Where(a => a.PeriodCentiseconds > 0).ToList();
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
