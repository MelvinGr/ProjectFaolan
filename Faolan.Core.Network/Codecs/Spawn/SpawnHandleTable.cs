using System.Collections.Generic;
using System.Threading;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Per-session allocator that assigns a unique entity handle to each spawned entity (the player and each NPC)
	// and remembers the mapping, so the same entity keeps a stable handle across the spawn, its 96C46740
	// registration, the 0x2020 -> 0x2008 spawn-check, and the 0x2076 manifest. Retail assigns these server-side
	// (e.g. 0x000818xx, 0x00081906); the exact values are not significant as long as they are unique within the
	// session and distinct from the player's client instance.
	//
	// Phase-2 foundation: once spawns are built dynamically (instead of replayed verbatim with the capture's
	// handles), every builder draws its handle from here, which is what lets the login path drop the Strie-only
	// gating and work for any character/NPC set.
	public sealed class SpawnHandleTable
	{
		// Base chosen to sit in the same low range retail uses while staying clear of character instances
		// (which are >= 0x02000000). Allocation is sequential from here.
		public const uint HandleBase = 0x00090000;
		private readonly Dictionary<string, uint> _byKey = [];

		private readonly Lock _lock = new Lock();
		private uint _next = HandleBase;

		// Allocates a fresh, unique handle.
		public uint Allocate()
		{
			lock (_lock)
			{
				return _next++;
			}
		}

		// True if a handle has been assigned to this key.
		public bool Contains(string entityKey)
		{
			lock (_lock)
			{
				return _byKey.ContainsKey(entityKey);
			}
		}

		// Returns the handle for the given entity key, allocating one on first use. Stable for the session.
		public uint GetOrAllocate(string entityKey)
		{
			lock (_lock)
			{
				if (_byKey.TryGetValue(entityKey, out var existing))
					return existing;

				var handle = _next++;
				_byKey[entityKey] = handle;
				return handle;
			}
		}
	}
}
