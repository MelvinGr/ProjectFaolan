using System.Collections.Generic;

namespace Faolan.Core.Network.Server.GameServer
{
	/// <summary>
	///     One entity in a live map zone, as exposed to out-of-band tooling (the MCP list_zone_entities tool).
	///     A read-only projection of the world model's entity state; Kind is "player", "npc" or "loot".
	/// </summary>
	public sealed record ZoneEntityInfo(uint Handle, string Name, string Kind, uint Level, bool Hostile, bool Dead, uint? Health, uint? MaxHealth, float X, float Y, float Z)
	{
		public string HandleHex => $"0x{Handle:X8}";
	}

	/// <summary>
	///     A connected character's live world state (the authoritative in-memory view, as opposed to the DB row):
	///     transform + map, vitals and server-tracked buffs. Null fields mean the world layer doesn't track them.
	/// </summary>
	public sealed record CharacterLiveState(uint CharacterId, string Name, uint Handle, uint MapId, uint PlayfieldToken, float X, float Y, float Z, uint? Health, uint? MaxHealth,
		IReadOnlyList<string> ActiveBuffs)
	{
		public string HandleHex => $"0x{Handle:X8}";
	}

	// Resolves an inbound GameServer RPC (receiver class id, 16-bit method opcode) to its recovered method name
	// for diagnostic logging. The GameServer connection multiplexes several RPC class-pairs, so the SAME 0x20xx
	// opcode means different methods per class - the receiver endpoint's class id is the disambiguator (memory
	// protocol_gameserver_rpc_multiplexing). Used by the listeners' default branch so an unhandled packet logs
	// its method name instead of bare hex. Returns "Unknown" when the (class, opcode) pair has no recovered name.
}
