using System;
using System.Buffers.Binary;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The despawn / "entity left your view" packet - n3ToClientQuit (0x04B82B2D). DECODED 2026-06-11 from all 3271
	// retail records (server->client only); see protocol_despawn_n3toclientquit. The 0x2000 IIR body is:
	//   [subop=04B82B2D][class][handle = entity to remove][0x01 marker][u32 playfield-instance token]
	// ⚠️ The second word is the removed object's CLASS (EntityClass), NOT a constant ack. The client indexes the
	// removal by (class, handle), so a despawn sent with the wrong class silently misses the object (#366): a static
	// world object is a SimpleDynel 0xC878 / a loot bag 0xC87A / a door 0xC748, not Character 0xC350. Players/NPCs are
	// Character. (The 3271-record decode only ever looked at Character despawns, which is why it read as a constant.)
	// The marker is always 0x01. The token is NOT a handle - it's the per-playfield id the server announces at
	// world-entry (woven into the 0x737A6DF9 snapshot tails, between markers 000000BF and 0000040B) and stamps into
	// every despawn so the client scopes the removal to that playfield instance. We extract it from the snapshot we
	// actually send (TryExtractPlayfieldToken), so the despawn token matches what the client learned, by construction.
	public static class WorldDespawn
	{
		public const int BodyLength = 17; // [subop][typeTag][handle][marker][token] = 4+4+4+1+4

		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.N3ToClientQuitIIR;
		private const byte RecordMarker = 0x01; // constant in 3271/3271 records (record-count / type marker)

		// The markers that bracket the playfield token inside the 0x737A6DF9 snapshot tail: ...000000BF <token> 0000040B
		// (verified identical in the seeded beach burst (token 0x004FE428) and the hoxxling gameplay capture (0x01EA985C)).
		private static readonly byte[] TokenPrefixMarker = [0x00, 0x00, 0x00, 0xBF];
		private static readonly byte[] TokenSuffixMarker = [0x00, 0x00, 0x04, 0x0B];

		// Builds the 17-byte IIR body (the bytes after the u32 length prefix) for removing `handle` (of class
		// `classWord`) in playfield `token`. classWord defaults to Character (players/NPCs); a static world object must
		// pass its own class (WorldObjectBuilder.TypeTagForClass) or the client won't remove it (#366).
		public static byte[] BuildBody(uint handle, uint token, uint classWord = EntityClass.Character)
		{
			return new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.N3ToClientQuitIIR).WriteUInt32(classWord).WriteUInt32(handle).WriteByte(RecordMarker).WriteUInt32(token).ToArray();
		}

		// Finds the playfield token in a 0x737A6DF9 snapshot body: the 4 bytes framed by 000000BF ... 0000040B
		// (prefix immediately precedes the token, suffix immediately follows it). Returns false if the framing isn't
		// present (then we don't know the token and must not send a despawn with a guessed value).
		public static bool TryExtractPlayfieldToken(ReadOnlySpan<byte> snapshotBody, out uint token)
		{
			return TryLocateFramedToken(snapshotBody, out token, out _);
		}

		// The inverse of BuildBody: parses a 17-byte n3ToClientQuit body into its (handle, token). Returns false when
		// the length, sub-opcode, ack TypeTag, or record marker don't match the decoded grammar (so a mis-routed body
		// is rejected rather than yielding garbage). Proven against the captured records by WorldDespawnTests round-trip.
		// Read sequentially: the old offsets ran 0 -> 4 -> 8 -> 13, and that 8 -> 13 step was the only trace of the
		// single-byte record marker between the handle and the token.
		public static bool TryParseBody(byte[] body, out uint handle, out uint token)
		{
			handle = 0;
			token = 0;
			if (body is not { Length: BodyLength })
				return false;

			var r = new FaolanStream(body);
			if (r.ReadUInt32() != (uint)GameServer0X2000Opcodes.N3ToClientQuitIIR)
				return false;

			r.ReadUInt32(); // class - a field (Character / SimpleDynel / LootBag / ...), NOT a constant (#366)

			var parsedHandle = r.ReadUInt32();
			if (r.ReadByte() != RecordMarker)
				return false;

			handle = parsedHandle;
			token = r.ReadUInt32();
			return true;
		}

		// (The playfield token is now authored INTO the snapshot at build time - FullCharSnapshotSubClass writes the live
		// zone token into stat slot 0xBF - so the former TryReplacePlayfieldToken in-place stamp is retired.)

		private static bool TryLocateFramedToken(ReadOnlySpan<byte> snapshotBody, out uint token, out int tokenStart)
		{
			token = 0;
			tokenStart = -1;
			var i = snapshotBody.IndexOf(TokenPrefixMarker);
			while (i >= 0 && i + 12 <= snapshotBody.Length)
			{
				var start = i + 4;
				if (snapshotBody.Slice(start + 4, 4).SequenceEqual(TokenSuffixMarker))
				{
					token = BinaryPrimitives.ReadUInt32BigEndian(snapshotBody.Slice(start, 4));
					tokenStart = start;
					return true;
				}

				var next = snapshotBody.Slice(i + 1).IndexOf(TokenPrefixMarker);
				i = next < 0
					? -1
					: i + 1 + next;
			}

			return false;
		}
	}
}
