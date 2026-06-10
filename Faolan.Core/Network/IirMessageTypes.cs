using System.Collections.Generic;

namespace Faolan.Core.Network
{
	// The AoC world build is a stream of IIR (Incremental Information Replication) records, each tagged with a
	// message-type id (the 0x2000 sub-opcode). This is the recovered catalog (names from the client .proto schema)
	// plus the classification that drives world-burst storage: SERVER-GENERATED records (globals + the player's own
	// state) are NOT stored in the per-map burst - the server builds them from the live map/character on world entry
	// - while everything else (world entities) is stored as MapWorldBurstEntity rows. See IirRecordCodec.
	public static class IirMessageTypes
	{
		// Global records (handle 0) - server-generated.
		public const uint MapChange = 0x5D85BFC7;
		public const uint GameTime = 0x4F57DC08;

		// Player-state records (handle == the player) - server-generated from the live character.
		public const uint StatIir = 0xF98E10B3;
		public const uint Vitals = 0x96B8DC59;
		public const uint FullChar = 0x737A6DF9;

		// World-entity records (handle == some other entity) - stored per map.
		public const uint SimpleCharFullUpdate = 0xFA015807; // NPCs (and the player's own spawn)
		public const uint SimpleDynelFullUpdate = 0x66AEDD50;
		public const uint LootBagFullUpdate = 0xF096251E;
		public const uint MoveTask = 0x96C46740;
		public const uint BehaviorControlCenter = 0x642CD3D6;
		public const uint DoorFullUpdate = 0x33A56FB0;
		public const uint GameActionList = 0xF508F4C1;

		// Global records the server always generates (handle 0) - never stored in any burst.
		private static readonly HashSet<uint> GlobalGeneratedTypes = [MapChange, GameTime];

		// Player-state records the server generates from the live character - server-generated only when the record
		// targets the player; the SAME type targeting another entity (e.g. an NPC's vitals) is real world data and is
		// kept. Both sets grow as each generator lands (see WorldEntryGeneratedRecords).
		private static readonly HashSet<uint> PlayerGeneratedTypes = [Vitals];

		// True if this record is server-generated on world entry (so it must NOT be stored in the burst). Globals are
		// always generated; player-state types only when they target the player instance.
		public static bool IsServerGenerated(uint iirType, uint handle, uint playerInstance) =>
			GlobalGeneratedTypes.Contains(iirType) || (handle == playerInstance && PlayerGeneratedTypes.Contains(iirType));
	}
}
