using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     Locates the known-spell list inside a raw FullChar snapshot body - a different question from decoding one,
	///     which is why it is a neighbour rather than a second <c>Parse</c> on the codec.
	/// </summary>
	/// <remarks>
	///     The importer needs it because the list sits inside the post-access block's flat u32 view, so there is no
	///     structural path down to it without deserializing tails a high-level caster can trip.
	/// </remarks>
	public static class KnownSpellListScan
	{
		// Keys on the list's own structure - a count word that is a valid frame with at least one record, whose first record
		// carries the 0xFFFFFFFF slot word - rather than on the preceding stat-block landmark, whose values shift with level.
		// That frame check and the slot word together are highly selective, and the codec then validates every record's slot
		// word, so a false hit is rejected. Returns the largest valid list (the real spellbook; a snapshot can carry a
		// smaller unrelated 0xFFFFFFFF-framed list too), or false when none is found.
		public static bool TryExtractFromSnapshot(ReadOnlySpan<byte> body, [NotNullWhen(true)] out KnownSpellList? list)
		{
			list = null;
			for (var i = 0; i + 4 + KnownSpellListCodec.RecordSize <= body.Length; i++)
			{
				if (!ListFrame.TryCountOf(body.ReadU32Be(i), out var count) || count < 1)
					continue; // not a framed list with at least one record

				if (body.ReadU32Be(i + 4 + 4) != KnownSpellListCodec.Unbound)
					continue; // first record's slot word isn't the known-list marker

				var end = i + 4 + count * KnownSpellListCodec.RecordSize;
				if (end > body.Length)
					continue;

				try
				{
					var candidate = KnownSpellListCodec.Parse(body[i..end]);
					if (list == null || candidate.Records.Count > list.Records.Count)
						list = candidate;
				}
				catch (CodecParseException)
				{
					// A later record failed to validate, so this is not the known list; keep scanning.
				}
			}

			return list != null;
		}
	}
}
