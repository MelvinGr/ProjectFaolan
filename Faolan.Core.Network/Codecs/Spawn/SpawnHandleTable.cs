using System.Collections.Generic;
using System.Threading;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Per-session allocator that assigns a unique entity handle to each spawned entity - the player and each NPC - and
	///     remembers the mapping, so an entity keeps a stable handle across its spawn, its registration, the spawn-check
	///     exchange and the manifest.
	/// </summary>
	/// <remarks>
	///     Retail assigns these server-side; the exact values are not significant as long as they are unique within the
	///     session and distinct from the player's client instance. Every builder draws its handle from here.
	/// </remarks>
	public sealed class SpawnHandleTable
	{
		/// <summary>
		///     The allocation base, chosen to sit in the same low range retail uses while staying clear of character
		///     instances (EntityHandles.CharacterInstanceBase). Allocation is sequential from here, and staying below that
		///     base is the invariant that keeps a spawned entity from colliding with a character; asserted in
		///     SpawnHandleTableTests.
		/// </summary>
		public const uint HandleBase = 0x00090000;
		private readonly Dictionary<string, uint> _byKey = [];

		private readonly Lock _lock = new Lock();
		private uint _next = HandleBase;

		/// <summary>Allocates a fresh, unique handle.</summary>
		public uint Allocate()
		{
			lock (_lock)
			{
				return _next++;
			}
		}

		/// <summary>True if a handle has been assigned to this key.</summary>
		public bool Contains(string entityKey)
		{
			lock (_lock)
			{
				return _byKey.ContainsKey(entityKey);
			}
		}

		/// <summary>
		///     Returns the handle for the given entity key, allocating one on first use. Stable for the session.
		/// </summary>
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
