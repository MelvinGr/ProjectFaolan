using System;
using System.Buffers.Binary;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Packets.FullCharSnapshot;

namespace Faolan.GameServer.World
{
	// Builds the 0x96B8DC59 entity vitals / stat-update packet - the moment-to-moment counterpart of the big
	// 0x737A6DF9 snapshot's stat block. Confirmed wire layout (docs/archive/quests-and-combat.md):
	//   [subop 96B8DC59][0000C350][entityHandle:4][00000000:4][count:1][count × (statId:u32, value:u32)]
	// Shared by everything that pushes stat/vitals changes (combat, the regen tick, the world-entry replay, GM
	// commands), framed on the world conversation (WorldFraming). Pulled out of the listener so the CombatService can
	// build it without depending on the listener type.
	public static class VitalsPacket
	{
		private const uint VitalsMarker = EntityClass.Character;

		// Builds a 0x96B8DC59 stat block for an entity (defaults to the session's own client instance). Returns the
		// wire bytes.
		public static byte[] BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint? entityHandle = null)
		{
			var handle = entityHandle ?? client.Session.Account.ClientInstance;

			var ps = new PacketStream();
			ps
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000RespondsOpcodes.StatUpdateIIR);
					s.WriteUInt32(VitalsMarker);
					s.WriteUInt32(handle);
					s.WriteUInt32(0x00000000);
					stats.WriteTo(s);
				});
			return ps
				.WriteLengthHash()
				.ToArray();
		}

		// The inverse of BuildStatUpdate at the IIR-body level: parses the inner 0x96B8DC59 record
		//   [subop 96B8DC59][0000C350][entityHandle:4][00000000:4][CharacterStatBlock]
		// (the bytes WriteArrayUInt32Length wraps, i.e. after the u32 length prefix). Returns the entity handle and the
		// shared stat block. Validates the sub-opcode + marker so a mis-routed body is rejected. Proven by the
		// VitalsPacket round-trip in CharacterStatBlockTests.
		public static bool TryParseStatUpdateBody(ReadOnlySpan<byte> body, out uint entityHandle, out CharacterStatBlock stats)
		{
			entityHandle = 0;
			stats = new CharacterStatBlock();
			if (body.Length < 16)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000RespondsOpcodes.StatUpdateIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != VitalsMarker)
				return false;

			entityHandle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			// body[12..16] is the constant 0x00000000 padding word; the stat block follows.
			return CharacterStatBlock.TryParse(body.Slice(16), out stats, out _);
		}
	}
}
