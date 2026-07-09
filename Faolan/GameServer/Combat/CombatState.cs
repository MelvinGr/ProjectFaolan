using System;
using System.Collections.Concurrent;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     In-memory store of live entity vitals keyed by entity handle. The world is authoritative in memory, so
	///     this is never persisted. Player entries are seeded from <c>CharacterMaxStats</c>; NPC entries are lazily
	///     created with a default max the first time they are hit (real NPC max HP isn't decoded from captures yet).
	/// </summary>
	public sealed class CombatState
	{
		private readonly ConcurrentDictionary<uint, EntityVitals> _vitals = new();

		public EntityVitals GetOrAdd(uint handle, Func<EntityVitals> factory)
		{
			return _vitals.GetOrAdd(handle, _ => factory());
		}

		public void Remove(uint handle)
		{
			_vitals.TryRemove(handle, out _);
		}

		public void Set(uint handle, EntityVitals vitals)
		{
			_vitals[handle] = vitals;
		}

		public bool TryGet(uint handle, out EntityVitals vitals)
		{
			return _vitals.TryGetValue(handle, out vitals);
		}
	}
}
