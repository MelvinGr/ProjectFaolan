using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.CreateFlow;

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

		// 0x5D85BFC7 MapChange = the PlayfieldFIIR: tells the client which playfield/instance the player is entering.
		// This is now a thin wrapper over the shared CreateEditorRecordBuilder.BuildPlayfieldFiir codec (the create-flow
		// builds the SAME packet) - it just prepends the MapChange subop and sources the PER-MAP rule fields from the Map
		// row (#308). The client keys per-area rules on this packet (mount permission = PlayfieldFlags); those fields are
		// NOT universal (beach vs mainland differ), so they come from the DB (Map.PlayfieldCount/FieldB/Flags/Instanced),
		// defaulting to the beach values for un-sourced maps. Position is the character's entry transform (per-session);
		// nearClip 15.0 + the farClip float are camera params (left constant). `map` may be null (fresh/unmapped -> beach
		// defaults).
		internal static byte[] BuildMapChange(INetworkClient client, Map map, uint playfieldToken)
		{
			var character = client.Session.Character;
			var charInstance = (map?.PlayfieldInstanced ?? true) ? client.Session.Account.ClientInstance : 0u;
			var count = map?.PlayfieldCount ?? 2u;
			var fieldB = map?.PlayfieldFieldB ?? 3u;
			var flags = map?.PlayfieldFlags ?? 0u;

			var ps = new PacketStream();
			ps
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(IirMessageTypes.MapChange);
					s.WriteArray(CreateEditorRecordBuilder.BuildPlayfieldFiir(
						character.PositionX, character.PositionY, character.PositionZ,
						character.MapId, charInstance, count, playfieldToken,
						nearClip: 15.0f, farClip: BitConverter.UInt32BitsToSingle(0x40E3944Fu),
						flags: flags, field58: 0x01000000u, fieldB: fieldB));
				});
			return ps.WriteLengthHash().ToArray();
		}
	}
}
