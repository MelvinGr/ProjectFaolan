namespace Faolan.Core.Constants
{
	/// <summary>
	///     The layout of the entity-handle number line.
	/// </summary>
	/// <remarks>
	///     A character id is its on-wire client instance - the same number identifies the row in the database and the
	///     entity in the world - which is why this lives in the base layer rather than in the network or database one:
	///     both sides need the same boundary, and Faolan.Core.Database cannot see Faolan.Core.Network.
	///     <para />
	///     Three consequences, one constant:
	///     <list type="bullet">
	///         <item>characters are allocated from <see cref="CharacterInstanceBase" /> upward (DatabaseRepository);</item>
	///         <item>
	///             a session's spawned entities (NPCs, world objects, loot) must be allocated below it so they cannot
	///             collide with a character (SpawnHandleTable.HandleBase = 0x00090000, in the same low range retail uses);
	///         </item>
	///         <item>
	///             a capture scan identifies the player's own spawn by its handle being at or above it, since every
	///             other spawn in the burst is an NPC with a small handle (NpcSpawnExtractor, PatrolPathExtractor).
	///         </item>
	///     </list>
	/// </remarks>
	public static class EntityHandles
	{
		/// <summary>
		///     Characters (= client instances) start here. Entity handles allocated per session stay below it.
		/// </summary>
		public const uint CharacterInstanceBase = 0x02000000;

		/// <summary>
		///     The largest character id that encodes to at most 4 protobuf-varint bytes.
		/// </summary>
		/// <remarks>
		///     The burst replay substitutes character ids into captured 4-byte protobuf-varint slots, so an id must
		///     encode to at most 4 varint bytes. A 5-byte id (above 0x0FFFFFFF) crashes the client.
		/// </remarks>
		public const uint MaxCharacterId = 0x0FFFFFFF;
	}
}
