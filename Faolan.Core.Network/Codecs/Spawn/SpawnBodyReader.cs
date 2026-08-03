using System;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Reads what a CAPTURED FA015807 (SimpleCharFullUpdate) body carries. Not a codec - the encoding lives in
	// SimpleCharFullUpdateCodec; this is the boundary where a raw captured body (which still carries the u32 carrier
	// length the codec does not own, #530 rule 2) becomes a question the importer/tests can ask.
	//
	// TWO readers, deliberately, because they answer different questions:
	//   - TryParse       decodes the WHOLE record through the codec. Strict: a body it cannot account for is a
	//                    `false`, not a partial value.
	//   - TryReadCharacterMessage SCANS for the embedded ProtoCoreCharacterIIR alone. It works on a body the codec
	//                    cannot parse - a fragment, a foreign layout, a 66AEDD50 dynel - which is why the importer's
	//                    identity/equipment sweeps still use it. The message is a length-delimited block: a 4-byte
	//                    big-endian length immediately precedes its first protobuf field, field 1 (name) =
	//                    0x0A <len> <utf8>. We scan for that signature (the character name is the first printable
	//                    string in the body), verify the preceding length decodes, and return it.
	public static class SpawnBodyReader
	{
		// Decodes a captured body's whole record. `body` INCLUDES the leading u32 carrier length, which is stripped
		// here. False when the body is not a spawn this codec can account for - which is an ordinary outcome for a
		// caller sweeping a mixed capture, and is why this is a Try rather than a bare Parse.
		public static bool TryParse(byte[] body, out SimpleCharFullUpdateIIR spawn)
		{
			spawn = null!;
			if (body == null || body.Length < 8)
				return false;

			try
			{
				spawn = SimpleCharFullUpdateCodec.Parse(body.AsSpan(4));
				return true;
			}
			catch (CodecParseException)
			{
				return false;
			}
		}

		// Whether a decoded spawn is a PLAYER's: no move-task slot in its first record (a player's first record IS
		// the buff list's framing word, length 4) and the player's own word2. Both were bare literals in the old
		// player-only codec, which is how they came to read as protocol constants - an NPC carries 5 and 1/2/3.
		public static bool IsPlayer(SimpleCharFullUpdateIIR spawn)
		{
			return spawn.Post is { HasMoveTaskSlot: false, MovementMode: SpawnPostIirs.PlayerMovementMode } && spawn.Post.Slot is PlayerEntrySlot;
		}

		public static bool TryReadCharacterMessage(byte[] body, out Result result)
		{
			result = default;
			if (body == null || body.Length < 8)
				return false;

			for (var i = 4; i < body.Length - 2; i++)
			{
				if (body[i] != 0x0A) // field 1 (name), wiretype 2
					continue;

				int nameLen = body[i + 1];
				if (nameLen is < 1 or > 63 || i + 2 + nameLen > body.Length)
					continue;
				if (!IsPrintableAscii(body, i + 2, nameLen))
					continue;

				var messageLength = (int)body.ReadU32Be(i - 4);
				if (messageLength < nameLen + 2 || i + messageLength > body.Length)
					continue;

				try
				{
					var message = body.AsSpan(i, messageLength).FromProtobuf<ProtoCoreCharacterIIR>();
					if (!string.IsNullOrEmpty(message.Name) && message.Name.Length == nameLen)
					{
						result = new Result(i, messageLength, message);
						return true;
					}
				}
				catch
				{
					// not the character message - keep scanning
				}
			}

			return false;
		}

		private static bool IsPrintableAscii(byte[] data, int start, int length)
		{
			for (var i = start; i < start + length; i++)
			{
				if (data[i] < 0x20 || data[i] > 0x7E)
					return false;
			}

			return true;
		}

		public readonly record struct Result(int Offset, int Length, ProtoCoreCharacterIIR Message);
	}
}
