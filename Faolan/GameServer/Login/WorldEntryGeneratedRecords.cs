using Faolan.Core.Network;
using Faolan.Core.Network.Packets;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// World-entry IIR records the server GENERATES from the live map/character instead of replaying captured bytes.
	// These are the global / player-specific records that are NOT per-map world content: the DB world burst stores
	// only world entities (handle != 0 and != the player), and the records here are emitted around that replay.
	// Framing is identical to the captured records (LoginWorldSender = world-system 0x13CE71B1/0x58, receiver = the
	// player), and the body is built field-by-field (big-endian) so a record built with the captured map+instance is
	// byte-identical to its capture - see WorldEntryGeneratedRecordsTests.
	public partial class GameServerListener
	{
		// 0x5D85BFC7 MapChange - tells the client which playfield/instance the player is entering. Built from the
		// character's map + live instance; the remaining fields are the map-build constants from the world capture
		// (playfield 0x004FE428, view/fog constants, ...). TODO: source the per-map constants from the Map table so
		// this generalises beyond the beach (currently only the beach burst omits its MapChange).
		internal static byte[] BuildMapChange(INetworkClient client)
		{
			var mapId = client.Session.Character.MapId ?? 0;
			var instance = client.Session.Account.ClientInstance;

			var ps = new PacketStream();
			ps.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x00002000).WriteArrayUInt32Length(s =>
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
				s.WriteUInt32(mapId);       // map (beach = 0x00000FAA)
				s.WriteUInt32(0x00000000);
				s.WriteUInt32(instance);    // the live player instance
				s.WriteUInt32(0x00000000);
				s.WriteUInt32(0x00000002);
				s.WriteUInt32(0x00009C50);
				s.WriteUInt32(0x004FE428);  // playfield id
				s.WriteUInt32(0x00000000);
				s.WriteByte(0x01);
				s.WriteUInt32(0x00000018);
				s.WriteUInt32(0x00000001);
				s.WriteUInt32(0x004FE428);  // playfield id
				s.WriteUInt32(0x0000CB20);
				s.WriteUInt32(0x41700000);  // float 15.0
				s.WriteUInt32(0x40E3944F);  // float 7.1111
				s.WriteUInt32(0x00000003);
				s.WriteUInt32(0xFFFFFFFF);
				s.WriteUInt32(0xFFFFFFFF);
				s.WriteUInt32(0x00000007);
				s.WriteUInt32(0x00000000);
			});
			return ps.WriteLengthHash().ToArray();
		}

		// 0x4F57DC08 GameTime - the world clock. Global (no map/player data); the client advances it locally, so a
		// fixed snapshot is emitted. TODO: derive the time fields from the server clock for a live time-of-day.
		internal static byte[] BuildGameTime(INetworkClient client)
		{
			var ps = new PacketStream();
			ps.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x00002000).WriteArrayUInt32Length(s =>
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
	}
}
