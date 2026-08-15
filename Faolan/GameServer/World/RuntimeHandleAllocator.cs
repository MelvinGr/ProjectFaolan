using System.Threading;

namespace Faolan.GameServer.World
{
	// Monotonic allocator for ALL runtime-spawned entity handles - dropped-loot bags, pets, summoned NPCs,
	// and the #363 ambient pool members (not loot-specific despite the range's origin). CRITICAL: the
	// value must be in the SAME LOW range retail assigns mid-session entity handles - the client only tracks/polls
	// handles in that range, and the render of a mid-session spawn is completed by the client polling 0x2020 for the
	// entity's handle (-> server SpawnCheck 0x2008). Captured retail mid-session spawns (the mount + interest NPCs)
	// use 0x000286xx; the burst NPCs use 0x000818xx; the player/char-id is ~0x0802xxxx. A high value (the old
	// 0x40000000, or even 0x0F000000) is FAR above any real handle, so the client never polls it and the entity
	// silently never renders. 0x00090000 sits just above the burst NPCs (0x000818xx) and well below the char-id
	// range (0x02000000+) - small, valid, collision-free. (Matches the RetailLoginReplay built-NPC test handle.)
	//
	// ⚠️ ONE allocator per game host, shared by every runtime spawner. It was a field on WorldBroadcaster until #826;
	// the moment a second spawner holds its own counter the two hand out the SAME handle and one entity silently
	// replaces the other in the zone's handle map. That is the whole reason this is a passed-around object rather
	// than a static: a realm process must not share the range with another realm in the same process either, which
	// `--role` already forbids (see multi-realm, #324).
	public sealed class RuntimeHandleAllocator
	{
		private int _next = 0x00090000;

		public uint Next()
		{
			return (uint)Interlocked.Increment(ref _next);
		}
	}
}
