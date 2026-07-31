using System;
using System.Text;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// LENIENT entity-name reads out of a captured spawn body - the shape `PlayfieldTokenScan` and `BehaviorTailScan`
	// set: a named read of one field out of a region, for callers that cannot afford a strict whole-record parse.
	//
	// `SpawnBodyReader.TryReadCharacterMessage` is the right reader when the whole CoreCharacter message decodes; both
	// of these exist because the importer's capture sweeps must still name a spawn whose message does not (a fragment,
	// a foreign layout). They scan for the message's field 1 - `0x0A <len> <printable ascii>` - which is the character
	// name and the first printable string in the body.
	//
	// ⚠️ The two differ, and that difference is NOT decoded - they are the two spellings the importer grew
	// independently, moved here so the divergence is visible in one place instead of in two extractors. `Any` starts
	// at 16 and takes 1..63 characters; `Plausible` starts at 40, takes 2..40 and requires a letter. Reconciling them
	// changes which spawns get names, so it is a decode question, not a refactor one: Gitea #541.
	public static class SpawnNameScan
	{
		// The first name-shaped run anywhere past the record header. Null when the body carries none.
		public static string? Any(ReadOnlySpan<byte> body)
		{
			return Scan(body, 16, 1, 63, false);
		}

		// The first name-shaped run past the transform, requiring at least one letter.
		public static string Plausible(ReadOnlySpan<byte> body)
		{
			return Scan(body, 40, 2, 40, true) ?? string.Empty;
		}

		private static string? Scan(ReadOnlySpan<byte> body, int from, int minLength, int maxLength, bool requireLetter)
		{
			for (var i = from; i + 2 < body.Length; i++)
			{
				if (body[i] != 0x0A) // field 1 (name), wiretype 2
					continue;

				int length = body[i + 1];
				if (length < minLength || length > maxLength || i + 2 + length > body.Length)
					continue;

				var slice = body.Slice(i + 2, length);
				if (IsPrintable(slice, requireLetter))
					return Encoding.ASCII.GetString(slice);
			}

			return null;
		}

		private static bool IsPrintable(ReadOnlySpan<byte> text, bool requireLetter)
		{
			var hasLetter = false;
			foreach (var c in text)
			{
				if (c is < 0x20 or > 0x7E)
					return false;

				if (c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')
					hasLetter = true;
			}

			return hasLetter || !requireLetter;
		}
	}
}
