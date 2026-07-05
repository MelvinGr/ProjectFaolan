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

		// 0x5D85BFC7 MapChange - tells the client which playfield/instance the player is entering. Built entirely from
		// the live character + shared token: the map id, the player's ENTRY POSITION, the live instance, and
		// WorldState.DefaultPlayfieldToken. Everything else is UNIVERSAL - the same bytes on every map. `--report
		// mapchange` proved this: across all captures the ONLY per-session variation is the position, so there are NO
		// per-map build constants and nothing here is beach-specific (the old hardcoded beach spawn was the one leak).
		internal static byte[] BuildMapChange(INetworkClient client, uint playfieldToken)
		{
			var character = client.Session.Character;
			var mapId = character.MapId;
			var instance = client.Session.Account.ClientInstance;

			var ps = new PacketStream();
			ps
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(IirMessageTypes.MapChange);
					s.WriteUInt32(0x00000000); // TypeTag (global record)
					s.WriteUInt32(0x00000000); // Handle (global record)
					// The record's "FieldA" slot + the body's first bytes are actually the player's ENTRY POSITION
					// straddling the framing: [pad 0x00][posX][posY][posZ big-endian floats]. This is the ONLY
					// per-session-varying field (--report mapchange), so it comes from the character, not a beach constant.
					s.WriteByte(0x00);
					s.WriteFloatBigEndian(character.PositionX);
					s.WriteFloatBigEndian(character.PositionY);
					s.WriteFloatBigEndian(character.PositionZ);
					// 0x62 = 'b', the version byte that opens the embedded PlayfieldProxy stream ([u8 'b'][u32x8][u8 bool] -
					// the client rejects any other version, docs/iir-readers-conversation-misc.md); the u32x8 descriptor
					// follows (0xC79C, mapId, instance, token, ...).
					s.WriteByte(0x62);
					s.WriteUInt32(0x0000C79C);
					s.WriteUInt32(mapId); // map (beach = 0x00000FAA)
					s.WriteUInt32(0x00000000);
					// #308: these two PlayfieldProxy words are NOT universal - the beach capture carries [instance][2] but
					// the retail Old Tarantia (map 2000) capture carries [0][1]. They look like the playfield-instance +
					// mode the client uses to decide whether to (re)load the playfield; sending the beach pair on a non-beach
					// map appears to leave the client on the beach playfield definition (splash 4010 -> no-mount flags).
					// Match the capture per-map: beach keeps [instance][2] (golden-verified), others send [0][1].
					var onBeach = mapId == Faolan.Core.Database.Tables.Map.TortageBeachId;
					s.WriteUInt32(onBeach ? instance : 0u);
					s.WriteUInt32(0x00000000);
					s.WriteUInt32(onBeach ? 0x00000002u : 0x00000001u);
					s.WriteUInt32(0x00009C50);
					s.WriteUInt32(playfieldToken); // playfield id (the one token every entry stamps)
					s.WriteUInt32(0x00000000);
					s.WriteByte(0x01);
					s.WriteUInt32(0x00000018);
					s.WriteUInt32(0x00000001);
					s.WriteUInt32(playfieldToken); // playfield id (the one token every entry stamps)
					s.WriteUInt32(0x0000CB20);
					s.WriteUInt32(0x41700000); // float 15.0
					s.WriteUInt32(0x40E3944F); // float 7.1111 (a per-session facing/scale float, left as-is)
					// #308: two more per-map PlayfieldFIIR flag words - beach = [0x3][...0x0], retail Old Tarantia (2000) =
					// [0x0][...0x1]. Candidates for the playfield-rule flag the mount check reads. Beach keeps its
					// golden-verified pair; non-beach maps send the retail Old Tarantia pair.
					s.WriteUInt32(onBeach ? 0x00000003u : 0x00000000u);
					s.WriteUInt32(0xFFFFFFFF);
					s.WriteUInt32(0xFFFFFFFF);
					s.WriteUInt32(0x00000007);
					s.WriteUInt32(onBeach ? 0x00000000u : 0x00000001u);
				});
			return ps.WriteLengthHash().ToArray();
		}
	}
}
