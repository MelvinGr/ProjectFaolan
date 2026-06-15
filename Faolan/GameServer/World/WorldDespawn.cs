using Faolan.Core.Network;
using System;
using System.Buffers.Binary;
using Faolan.Core.Data;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// The despawn / "entity left your view" packet - n3ToClientQuit (0x04B82B2D). DECODED 2026-06-11 from all 3271
	// retail records (server->client only); see protocol_despawn_n3toclientquit. The 0x2000 IIR body is:
	//   [subop=04B82B2D][typeTag/ack=0000C350][handle = entity to remove][0x01 marker][u32 playfield-instance token]
	// The marker is always 0x01. The token is NOT a handle - it's the per-playfield id the server announces at
	// world-entry (woven into the 0x737A6DF9 snapshot tails, between markers 000000BF and 0000040B) and stamps into
	// every despawn so the client scopes the removal to that playfield instance. We extract it from the snapshot we
	// actually send (TryExtractPlayfieldToken), so the despawn token matches what the client learned, by construction.
	public static class WorldDespawn
	{
		public const uint SubOpcode = (uint)GameServer0X2000RespondsOpcodes.N3ToClientQuitIIR; // n3ToClientQuitIIR_t
		public const uint TypeTagAck = EntityClass.Character; // the ack TypeTag shared by the IIR record family
		private const byte RecordMarker = 0x01; // constant in 3271/3271 records (record-count / type marker)

		// The markers that bracket the playfield token inside the 0x737A6DF9 snapshot tail: ...000000BF <token> 0000040B
		// (verified identical in the seeded beach burst (token 0x004FE428) and the hoxxling gameplay capture (0x01EA985C)).
		private static readonly byte[] TokenPrefixMarker = [0x00, 0x00, 0x00, 0xBF];
		private static readonly byte[] TokenSuffixMarker = [0x00, 0x00, 0x04, 0x0B];

		public const int BodyLength = 17; // [subop][typeTag][handle][marker][token] = 4+4+4+1+4

		// Builds the 17-byte IIR body (the bytes after the u32 length prefix) for removing `handle` in playfield `token`.
		public static byte[] BuildBody(uint handle, uint token)
		{
			return new FaolanStream()
				.WriteUInt32(SubOpcode)
				.WriteUInt32(TypeTagAck)
				.WriteUInt32(handle)
				.WriteByte(RecordMarker)
				.WriteUInt32(token)
				.ToArray();
		}

		// The inverse of BuildBody: parses a 17-byte n3ToClientQuit body into its (handle, token). Returns false when
		// the length, sub-opcode, ack TypeTag, or record marker don't match the decoded grammar (so a mis-routed body
		// is rejected rather than yielding garbage). Proven against the captured records by WorldDespawnTests round-trip.
		public static bool TryParseBody(ReadOnlySpan<byte> body, out uint handle, out uint token)
		{
			handle = 0;
			token = 0;
			if (body.Length != BodyLength)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != SubOpcode)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != TypeTagAck)
				return false;
			if (body[12] != RecordMarker)
				return false;

			handle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			token = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(13, 4));
			return true;
		}

		// Finds the playfield token in a 0x737A6DF9 snapshot body: the 4 bytes framed by 000000BF ... 0000040B
		// (prefix immediately precedes the token, suffix immediately follows it). Returns false if the framing isn't
		// present (then we don't know the token and must not send a despawn with a guessed value).
		public static bool TryExtractPlayfieldToken(ReadOnlySpan<byte> snapshotBody, out uint token)
		{
			return TryLocateFramedToken(snapshotBody, out token, out _);
		}

		// Overwrites the marker-framed playfield token in a snapshot tails/body buffer IN PLACE (used when assembling
		// tails from the server-owned template, whose baked-in token is the template capture's - the live map's token
		// must be stamped over it). Returns false (buffer untouched) when the framing isn't present.
		public static bool TryReplacePlayfieldToken(byte[] snapshotBody, uint token)
		{
			if (!TryLocateFramedToken(snapshotBody, out _, out var tokenStart))
				return false;

			BinaryPrimitives.WriteUInt32BigEndian(snapshotBody.AsSpan(tokenStart, 4), token);
			return true;
		}

		private static bool TryLocateFramedToken(ReadOnlySpan<byte> snapshotBody, out uint token, out int tokenStart)
		{
			token = 0;
			tokenStart = -1;
			var i = snapshotBody.IndexOf(TokenPrefixMarker);
			while (i >= 0 && i + 12 <= snapshotBody.Length)
			{
				var start = i + 4;
				if (snapshotBody
				    .Slice(start + 4, 4)
				    .SequenceEqual(TokenSuffixMarker))
				{
					token = BinaryPrimitives.ReadUInt32BigEndian(snapshotBody.Slice(start, 4));
					tokenStart = start;
					return true;
				}

				var next = snapshotBody
					.Slice(i + 1)
					.IndexOf(TokenPrefixMarker);
				i = next < 0
					? -1
					: i + 1 + next;
			}

			return false;
		}
	}
}
