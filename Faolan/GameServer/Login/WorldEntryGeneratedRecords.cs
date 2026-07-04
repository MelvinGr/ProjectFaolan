using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// World-entry IIR records the server GENERATES from the live map/character instead of replaying captured bytes.
	// These are the global / player-specific records that are NOT per-map world content (which is constructed from the
	// DB WorldObject/NpcSpawn tables); the records here are emitted around that world build.
	// Framing is identical to the captured records (LoginWorldSender = world-system 0x13CE71B1/0x58, receiver = the
	// player), and the body is built field-by-field (big-endian) so a record built with the captured map+instance is
	// byte-identical to its capture - see WorldEntryGeneratedRecordsTests.
	public partial class GameServerListener
	{
		// 0x4F57DC08 GameTime - the world clock. Global (no map/player data); the client advances it locally, so a
		// fixed snapshot is emitted. TODO: derive the time fields from the server clock for a live time-of-day.
		internal static byte[] BuildGameTime(INetworkClient client)
		{
			var ps = new PacketStream();
			ps
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(IirMessageTypes.GameTime);
					s.WriteUInt32(0x00000000); // TypeTag
					s.WriteUInt32(0x00000000); // Handle (global)
					s.WriteUInt32(0x01472BDD); // FieldA
					// --- body (17 bytes) ---
					s.WriteUInt32(0x00000000);
					s.WriteByte(0x01);
					s.WriteUInt64(0x0001920C6A024E93);
					s.WriteUInt32(0x3F800000); // float 1.0 (time rate)
				});
			return ps.WriteLengthHash().ToArray();
		}

		// 0x5D85BFC7 MapChange - tells the client which playfield/instance the player is entering. Built from the
		// character's map + live instance + the shared playfield token (WorldState.DefaultPlayfieldToken). The remaining
		// fields are opaque map-build constants (view/fog/...) currently lifted verbatim from the BEACH capture. They
		// DO vary per map (`--report mapchange`: FieldA + the offset-22/26 u32s differ across maps), so a non-beach map
		// gets the wrong ones today - a working-but-imperfect fallback. TODO: store the captured MapChange body per map
		// (Map table) and re-address the dynamic fields here, now feasible since we have non-beach captures.
		internal static byte[] BuildMapChange(INetworkClient client)
		{
			var mapId = client.Session.Character.MapId;
			var instance = client.Session.Account.ClientInstance;

			var ps = new PacketStream();
			ps
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(IirMessageTypes.MapChange);
					s.WriteUInt32(0x00000000); // TypeTag (global record)
					s.WriteUInt32(0x00000000); // Handle (global record)
					s.WriteUInt32(0x0043A140); // FieldA
					// --- body (91 bytes) ---
					s.WriteUInt16(0x0043);
					s.WriteUInt32(0x114FB344);
					s.WriteUInt32(0x30399A62);
					s.WriteUInt32(0x0000C79C);
					s.WriteUInt32(mapId); // map (beach = 0x00000FAA)
					s.WriteUInt32(0x00000000);
					s.WriteUInt32(instance); // the live player instance
					s.WriteUInt32(0x00000000);
					s.WriteUInt32(0x00000002);
					s.WriteUInt32(0x00009C50);
					s.WriteUInt32(World.WorldState.DefaultPlayfieldToken); // playfield id (the one token every entry stamps)
					s.WriteUInt32(0x00000000);
					s.WriteByte(0x01);
					s.WriteUInt32(0x00000018);
					s.WriteUInt32(0x00000001);
					s.WriteUInt32(World.WorldState.DefaultPlayfieldToken); // playfield id (the one token every entry stamps)
					s.WriteUInt32(0x0000CB20);
					s.WriteUInt32(0x41700000); // float 15.0
					s.WriteUInt32(0x40E3944F); // float 7.1111
					s.WriteUInt32(0x00000003);
					s.WriteUInt32(0xFFFFFFFF);
					s.WriteUInt32(0xFFFFFFFF);
					s.WriteUInt32(0x00000007);
					s.WriteUInt32(0x00000000);
				});
			return ps.WriteLengthHash().ToArray();
		}
	}
}
