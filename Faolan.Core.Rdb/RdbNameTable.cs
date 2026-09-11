using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     The client's global id-to-name table: RDB type 1000010, the single record id 1 (10.4 MB decompressed,
	///     193,431 entries / 187,490 distinct names).
	/// </summary>
	/// <remarks>
	///     It is what makes an opaque RDB id readable. Every asset, texture, mesh, playfield and server-side script
	///     the build knows about has a name here, keyed by the same id that other records reference it by - so a bare
	///     word in some record's payload resolves to <c>renownbystanderlovers</c> rather than staying a number.
	///     <para />
	///     Names are lowercased. The build pipeline folds case, so the table cannot give back the original spelling of
	///     a CamelCase identifier - which matters wherever a name is hashed rather than compared, because the client's
	///     hashes are case-sensitive (<see cref="Crypt.Elf28" />, <see cref="Crypt.Bkdr31" />). A name read from here
	///     is the right identifier and the wrong string for a hash. See <c>docs/subsystems/chat.md</c>.
	///     <para />
	///     Grammar (little-endian throughout), after a 24-byte header:
	///     <code>
	///     [u32 id][u32 width][width bytes: the NUL-terminated name, NUL-padded to `width`]
	///     </code>
	///     The record has a handful of internal breaks where the chain does not tile; the reader resyncs past them a
	///     word at a time rather than stopping, because stopping loses 100k names to a 4-byte hiccup (a walk that
	///     bails at the first break reads 92,421 of the 193,431 entries and looks complete).
	/// </remarks>
	public static class RdbNameTable
	{
		public const uint Type = 1000010;
		public const uint RecordId = 1;

		private const int HeaderBytes = 24;
		private const int MaxWidth = 512;

		/// <summary>Reads the whole id -&gt; name table out of an open RDB, or an empty map when it is not shipped.</summary>
		public static IReadOnlyDictionary<uint, string> Load(RdbReader rdb)
		{
			return rdb.TryGet(Type, RecordId, out var entry) && rdb.CanRead(entry)
				? Parse(rdb.ReadDecompressed(entry))
				: [];
		}

		public static Dictionary<uint, string> Parse(ReadOnlySpan<byte> body)
		{
			var names = new Dictionary<uint, string>();
			var at = HeaderBytes;
			while (at + 8 <= body.Length)
			{
				var id = body.ReadU32Le(at);
				var width = body.ReadU32Le(at + 4);
				if (width == 0 || width > MaxWidth || at + 8 + width > body.Length)
				{
					at += 4; // a break in the chain - step a word and try to pick it up again
					continue;
				}

				var field = body.Slice(at + 8, (int)width);
				var end = field.IndexOf((byte)0);
				var name = Ascii(end < 0 ? field : field[..end]);
				if (name == null)
				{
					at += 4;
					continue;
				}

				names[id] = name;
				at += 8 + (int)width;
			}

			return names;
		}

		// Null unless every byte is printable ASCII - the resync above uses that as its "is this really an entry"
		// test, so a lenient decode here would let it wander through binary as if it were names.
		private static string? Ascii(ReadOnlySpan<byte> bytes)
		{
			if (bytes.Length == 0)
				return null;

			var sb = new StringBuilder(bytes.Length);
			foreach (var b in bytes)
			{
				if (b is < 0x20 or > 0x7e)
					return null;

				sb.Append((char)b);
			}

			return sb.ToString();
		}
	}
}
