using System;
using System.Buffers.Binary;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The 0x96B8DC59 StatUpdateIIR body - the moment-to-moment counterpart of the big 0x737A6DF9 snapshot's stat
	// block, pushed whenever an entity's stats/vitals change (combat, the regen tick, the world-entry replay, GM
	// commands). Confirmed wire layout (docs/archive/quests-and-combat.md):
	//   [subop 96B8DC59][0000C350][entityHandle:4][00000000:4][CharacterStatBlock]
	// This is the pure IIR body only - no RPC header / length prefix. The host frames it per recipient
	// (VitalsPacket for a single addressed send, SendWorldIir when broadcasting to nearby viewers). Proven
	// byte-exact by the round-trip in CharacterStatBlockTests.
	public static class StatUpdateCodec
	{
		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.StatUpdateIIR;

		// The bare StatUpdateIIR body for an entity's stat change.
		public static byte[] BuildBody(uint entityHandle, CharacterStatBlock stats)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(GameServer0X2000Opcodes.StatUpdateIIR).WriteUInt32(EntityClass.Character).WriteUInt32(entityHandle).WriteUInt32(0x00000000);
			stats.WriteTo(s);
			return s.ToArray();
		}

		// The inverse of BuildBody: parses the inner 0x96B8DC59 record (the bytes after the u32 length prefix).
		// Returns the entity handle and the shared stat block. Validates the sub-opcode + marker so a mis-routed
		// body is rejected.
		public static bool TryParseBody(ReadOnlySpan<byte> body, out uint entityHandle, out CharacterStatBlock stats)
		{
			entityHandle = 0;
			stats = new CharacterStatBlock();
			if (body.Length < 16)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000Opcodes.StatUpdateIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != EntityClass.Character)
				return false;

			entityHandle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			// body[12..16] is the constant 0x00000000 padding word; the stat block follows.
			return CharacterStatBlock.TryParse(body.Slice(16), out stats, out _);
		}
	}
}
