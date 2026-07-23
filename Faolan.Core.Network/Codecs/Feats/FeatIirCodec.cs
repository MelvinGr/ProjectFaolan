using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Feats
{
	// FeatIIR (0x28DA68A0) - the feat TRAIN/unlock handshake. Body (starting at the sub-opcode; the caller frames it
	// with DispatchPassBlob + WriteArrayUInt32Length, which prepends the u32 length):
	//   [u32 FeatIIR][u32 0xC350][u32 charHandle][u8 0x00][u8 state][u32 0][u32 (count+1)*0x3F1][count x u32 featId]
	// state 0x04 = c2s train/unlock REQUEST, 0x05 = s2c ACK (echoes the requested id list) - the same 04->05 shape as
	// the claims/redeem window. The feat ids are the same id-space as the spawn's pre-IIR feat list (train order,
	// composite base*100+rank). Decoded from the hakotep feat captures; see docs/subsystems/feats.md + #251.
	public static class FeatIirCodec
	{
		public const byte StateAck = 0x05;

		public const byte StateTrainRequest = 0x04;
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FeatIIR;

		// [sub 4][C350 4][handle 4][00 1][state 1][zero 4][marker 4]
		private const int FeatsOffset = 22;

		// Build the wire body (from the sub-opcode; the u32 length prefix is added by the framing WriteArrayUInt32Length).
		public static byte[] Build(uint charHandle, byte state, IReadOnlyList<uint> featIds)
		{
			var s = new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(charHandle)
				.WriteByte(0x00)
				.WriteByte(state)
				.WriteUInt32(0u)
				.WriteUInt32(ListFrame.ForCount(featIds.Count));
			foreach (var id in featIds)
				s.WriteUInt32(id);

			return s.ToArray();
		}

		// Parse a FeatIIR body (from the sub-opcode). Returns false on a shape mismatch (wrong sub / bad list marker).
		//
		// Read SEQUENTIALLY: the old fixed offsets ran 0 -> 8 -> 13 -> 18, stepping silently over the 0xC350 type
		// word, the 0x00 byte before `state`, and the zero word before the list marker. Every field the record
		// carries is now named.
		public static bool TryParse(byte[] body, out uint charHandle, out byte state, [NotNullWhen(true)] out List<uint>? featIds)
		{
			charHandle = 0;
			state = 0;
			featIds = null;
			if (body is not { Length: >= FeatsOffset })
				return false;

			var r = new FaolanStream(body);
			if (r.ReadUInt32() != Opcode)
				return false;

			r.ReadUInt32(); // 0xC350 type word
			var handle = r.ReadUInt32();
			r.ReadByte(); // constant 0x00
			var parsedState = r.ReadByte();
			r.ReadUInt32(); // zero word

			var marker = r.ReadUInt32();
			if (marker == 0 || marker % ListFrame.Unit != 0)
				return false;

			var count = ListFrame.CountOf(marker);
			if (FeatsOffset + 4L * count > body.Length)
				return false;

			var ids = new List<uint>(count);
			for (var k = 0; k < count; k++)
				ids.Add(r.ReadUInt32());

			charHandle = handle;
			state = parsedState;
			featIds = ids;
			return true;
		}
	}
}
