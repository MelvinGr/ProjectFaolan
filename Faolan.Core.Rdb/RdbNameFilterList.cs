using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     The client's own forbidden-word list: RDB type 1065100, one record (19.4 KB decompressed, 1,819 entries /
	///     926 distinct). It is what gates a character name - see <c>NameFilterTerm</c> for why the import stores
	///     hashes rather than the words.
	/// </summary>
	/// <remarks>
	///     Grammar (little-endian throughout):
	///     <code>
	///     [u32 typeId = 1065100][u32 0][u32 1][u32 count]  then count x  [u32 byteLen][UTF-8 bytes]
	///     </code>
	///     The chain tiles the record exactly, so there is no resync case here and a short read is a real error. The
	///     first word of the header is the record's own type id, which is what makes the framing self-evident.
	///     <para />
	///     The list is multilingual - German, English, Spanish, French, Polish and a large Korean block - and most
	///     terms are listed twice, non-adjacently (the shape of a hash-table dump). Callers dedupe.
	/// </remarks>
	public static class RdbNameFilterList
	{
		public const uint Type = 1065100;
		public const uint RecordId = 0;

		private const int HeaderBytes = 16; // typeId, 0, 1, count

		/// <summary>Reads the forbidden-word list out of an open RDB, or an empty list when it is not shipped.</summary>
		public static IReadOnlyList<string> Load(RdbReader rdb)
		{
			return rdb.TryGet(Type, RecordId, out var entry) && rdb.CanRead(entry)
				? Parse(rdb.ReadDecompressed(entry))
				: [];
		}

		public static List<string> Parse(ReadOnlySpan<byte> body)
		{
			if (body.Length < HeaderBytes)
				throw new InvalidOperationException($"RDB {Type} is {body.Length} bytes - too short to carry its header.");

			var declared = body.ReadU32Le(12);
			var terms = new List<string>((int)Math.Min(declared, 100_000));
			var at = HeaderBytes;
			while (at + 4 <= body.Length)
			{
				var length = (int)body.ReadU32Le(at);
				at += 4;
				if (length < 0 || at + length > body.Length)
					throw new InvalidOperationException($"RDB {Type} entry {terms.Count} declares {length} bytes with {body.Length - at} left.");

				terms.Add(Encoding.UTF8.GetString(body.Slice(at, length)));
				at += length;
			}

			// The count is stated, so disagreeing with it is a decode error rather than something to paper over: a
			// reader that silently returns half a list makes an empty filter look like a clean one.
			if (terms.Count != declared)
				throw new InvalidOperationException($"RDB {Type} declares {declared} entries and the chain yields {terms.Count}.");

			return terms;
		}
	}
}
