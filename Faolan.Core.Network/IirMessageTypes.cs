using System.Collections.Generic;

namespace Faolan.Core.Network
{
	// The AoC world build is a stream of IIR (Incremental Information Replication) records, each tagged with a
	// message-type id (the 0x2000 sub-opcode). This is the recovered catalog (names from the client .proto schema)
	// plus the classification that drives world-burst storage: SERVER-GENERATED records (globals + the player's own
	// state) are NOT stored in the per-map burst - the server builds them from the live map/character on world entry
	// - while everything else (world entities) is decomposed into IirRecord (see IirRecordCodec).
	public static class IirMessageTypes
	{
		public const uint BehaviorControlCenter = 0x642CD3D6;
		public const uint DoorFullUpdate = 0x33A56FB0;
		public const uint FullChar = 0x737A6DF9;
		public const uint GameActionList = 0xF508F4C1;
		public const uint GameTime = 0x4F57DC08;

		public const uint LootBagFullUpdate = 0xF096251E;

		// Global records (handle 0) - server-generated.
		public const uint MapChange = 0x5D85BFC7;
		public const uint MoveTask = 0x96C46740;

		// World-entity records (handle == some other entity) - stored per map.
		public const uint SimpleCharFullUpdate = 0xFA015807; // NPCs (and the player's own spawn)
		public const uint SimpleDynelFullUpdate = 0x66AEDD50;

		// Player-state records (handle == the player) - server-generated from the live character.
		public const uint StatIir = 0xF98E10B3;
		public const uint Vitals = 0x96B8DC59;

		// Global records the server always generates (handle 0) - never stored in any burst.
		private static readonly HashSet<uint> GlobalGeneratedTypes = [MapChange, GameTime];

		// Player-state records the server is responsible for (not stored) when they target the player. The SAME type
		// targeting another entity (e.g. an NPC's vitals) is real world data and is kept. The server either emits a
		// generated version (Vitals, from the live MaxStats) or drops it as redundant (StatIir - incidental mid-stream
		// per-stat ticks of the captured player; the live player's stats are carried by the FullChar snapshot). Both
		// sets grow as each generator lands (see WorldEntryGeneratedRecords).
		private static readonly HashSet<uint> PlayerGeneratedTypes = [Vitals, StatIir];

		// Transient gameplay records (movement, look-at, leaving, combat buffs/damage, regen ticks, per-frame vitals).
		// They reference entities as if already live in the world, so replaying them during world ENTRY stalls the
		// client - a burst imported from a mid-session capture carries them, a clean login has none. They are never
		// part of the structural world build, so the importer drops them from the stored burst and the replay drops
		// any that slip through (the two filters share this set so they cannot drift). Vitals (0x96B8DC59) belong here
		// regardless of target: the player's are server-regenerated (BuildVitals) and an NPC's are per-frame ticks the
		// world build does not need.
		private static readonly HashSet<uint> TransientGameplayTypes =
		[
			0x4517AA81, // Move (CharDCMove)
			0x0D155695, // SelectDeselect (look-at)
			0x04B82B2D, // N3ToClientQuit (entity leaving)
			0x6B87E316, // MagicBuff (combat)
			0xE9F24184, // StatDamage (combat)
			Vitals, // 0x96B8DC59 - server-regenerated for the player, a per-frame tick for NPCs
			0x28D0AC99 // Regen (stat tick)
		];

		// True if this record is server-generated on world entry (so it must NOT be stored in the burst). Globals are
		// always generated; player-state types only when they target the player instance.
		public static bool IsServerGenerated(uint iirType, uint handle, uint playerInstance)
		{
			return GlobalGeneratedTypes.Contains(iirType) || (handle == playerInstance && PlayerGeneratedTypes.Contains(iirType));
		}

		// True if this record is a transient gameplay event (not part of the structural world build). Dropped from the
		// stored burst at import and again at replay (the replay drop is a no-op on an already-clean burst).
		public static bool IsTransientGameplay(uint iirType)
		{
			return TransientGameplayTypes.Contains(iirType);
		}
	}
}
