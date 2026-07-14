using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FeatIIR;

		public const byte StateTrainRequest = 0x04;
		public const byte StateAck = 0x05;

		private const uint AckConstant = 0xC350;
		private const uint ListMarker = 0x3F1;
		private const int FeatsOffset = 22; // [sub 4][c350 4][handle 4][00 1][state 1][zero 4][marker 4]

		// Build the wire body (from the sub-opcode; the u32 length prefix is added by the framing WriteArrayUInt32Length).
		public static byte[] Build(uint charHandle, byte state, IReadOnlyList<uint> featIds)
		{
			var s = new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(AckConstant)
				.WriteUInt32(charHandle)
				.WriteByte((byte)0x00)
				.WriteByte(state)
				.WriteUInt32(0u)
				.WriteUInt32(ListMarker * ((uint)featIds.Count + 1));
			foreach (var id in featIds)
				s.WriteUInt32(id);

			return s.ToArray();
		}

		// Parse a FeatIIR body (from the sub-opcode). Returns false on a shape mismatch (wrong sub / bad list marker).
		public static bool TryParse(byte[] body, out uint charHandle, out byte state, out List<uint> featIds)
		{
			charHandle = 0;
			state = 0;
			featIds = null;
			if (body == null || body.Length < FeatsOffset)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4)) != Opcode)
				return false;

			charHandle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8, 4));
			state = body[13];
			var marker = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(18, 4));
			if (marker == 0 || marker % ListMarker != 0)
				return false;

			var count = marker / ListMarker - 1;
			if (FeatsOffset + 4L * count > body.Length)
				return false;

			featIds = new List<uint>((int)count);
			for (var k = 0; k < count; k++)
				featIds.Add(BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(FeatsOffset + 4 * k, 4)));

			return true;
		}
	}
}
