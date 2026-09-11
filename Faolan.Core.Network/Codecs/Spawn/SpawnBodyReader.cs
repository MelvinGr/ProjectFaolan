using System;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Reads what a captured spawn body carries. Not a codec - the encoding lives in
	///     <see cref="SimpleCharFullUpdateCodec" /> - but the boundary where a raw captured body, which still carries the
	///     u32 carrier length the codec does not own, becomes a question the importer and the tests can ask.
	/// </summary>
	/// <remarks>
	///     There are deliberately two readers, answering different questions. <see cref="TryParse" /> decodes the whole
	///     record through the codec and is strict: a body it cannot account for is a false rather than a partial value.
	///     <see cref="TryReadCharacterMessage" /> scans for the embedded ProtoCoreCharacterIIR alone and works on a body the
	///     codec cannot parse - a fragment, a foreign layout, a dynel - which is why the importer's identity and equipment
	///     sweeps use it. The message is a length-delimited block whose 4-byte big-endian length immediately precedes its
	///     first protobuf field, field 1 (name), so the scan looks for that signature - the character name being the first
	///     printable string in the body - verifies the preceding length decodes, and returns it.
	/// </remarks>
	public static class SpawnBodyReader
	{
		/// <summary>
		///     Decodes a captured body's whole record. <paramref name="body" /> includes the leading u32 carrier length,
		///     which is stripped here. False when the body is not a spawn this codec can account for, which is an ordinary
		///     outcome for a caller sweeping a mixed capture and why this is a Try rather than a bare Parse.
		/// </summary>
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

		/// <summary>
		///     Whether a decoded spawn is a player's: no move-task slot in its first record - a player's first record is the
		///     buff list's framing word, four bytes long - and the player's own movement mode, where an NPC carries 1, 2 or
		///     3. The entry slot is one grammar for both families, so it does not discriminate.
		/// </summary>
		public static bool IsPlayer(SimpleCharFullUpdateIIR spawn)
		{
			return spawn.Post is { HasMoveTaskSlot: false, MovementMode: SpawnPostIirs.PlayerMovementMode };
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
