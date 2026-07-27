namespace Faolan.Core.Constants
{
	// The layout of the entity-handle number line.
	//
	// A character id IS its on-wire client instance - the same number identifies the row in the database and the
	// entity in the world - which is why this lives in the BASE layer rather than in the network or database one:
	// both sides need the same boundary, and Faolan.Core.Database cannot see Faolan.Core.Network.
	//
	// Three consequences, one constant:
	//   - characters are allocated from CharacterInstanceBase upward (DatabaseRepository);
	//   - a session's spawned entities (NPCs, world objects, loot) must be allocated BELOW it so they cannot
	//     collide with a character (SpawnHandleTable.HandleBase = 0x00090000, in the same low range retail uses);
	//   - a capture scan identifies the player's OWN spawn by its handle being at or above it, since every other
	//     spawn in the burst is an NPC with a small handle (NpcSpawnExtractor, PatrolPathExtractor).
	//
	// It had three names for one number - FirstCharacterId and InstanceRangeStart (twice) - plus three more places
	// that restated it in prose, so the third consequence above read as an unrelated magic number in the importer.
	public static class EntityHandles
	{
		// Characters (= client instances) start here. Entity handles allocated per session stay below it.
		public const uint CharacterInstanceBase = 0x02000000;

		// The burst replay substitutes character ids into captured 4-byte protobuf-varint slots, so an id must
		// encode to <= 4 varint bytes. A 5-byte id (> 0x0FFFFFFF) CRASHES the client.
		public const uint MaxCharacterId = 0x0FFFFFFF;
	}
}
