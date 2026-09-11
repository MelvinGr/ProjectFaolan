using System.Threading;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Monotonic allocator for every runtime-spawned entity handle: dropped loot bags, pets, summoned NPCs and the
	///     ambient pool members.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The value must sit in the same low range retail assigns mid-session entity handles, because the client
	///         only tracks and polls handles in that range, and the render of a mid-session spawn is completed by the
	///         client polling 0x2020 for the entity's handle, which the server answers with a 0x2008 SpawnCheck.
	///         Captured retail mid-session spawns (the mount and interest NPCs) use 0x000286xx, the burst NPCs
	///         0x000818xx, and a character id is around 0x0802xxxx. A high value such as 0x40000000 is far above any
	///         real handle, so the client never polls it and the entity silently never renders. 0x00090000 sits just
	///         above the burst NPCs and well below the character-id range: small, valid and collision-free.
	///     </para>
	///     <para>
	///         One allocator per game host, shared by every runtime spawner. The moment a second spawner holds its own
	///         counter the two hand out the same handle, and one entity silently replaces the other in the zone's
	///         handle map. That is why this is a passed-around object rather than a static: a realm process must not
	///         share the range with another realm in the same process either, which the role flag already forbids.
	///     </para>
	/// </remarks>
	public sealed class RuntimeHandleAllocator
	{
		private int _next = 0x00090000;

		public uint Next()
		{
			return (uint)Interlocked.Increment(ref _next);
		}
	}
}
