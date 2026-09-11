using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     Patches the client's content-integrity manifest, <c>RDBHashIndex.bin</c>.
	/// </summary>
	/// <remarks>
	///     At startup the client's ResourceManager validates each local resource's bytes against this manifest's
	///     expected md5; if they diverge it re-downloads the resource from the CDN and reverts le.idx. A replaced
	///     (upscaled) texture therefore only survives if the manifest's md5 and size are updated to the new blob -
	///     then local data hashes to the expected value and the entry is left alone.
	///     <para />
	///     <c>RDBHashIndex.bin</c> format (magic "RDHI", v6) is per-type sections of fixed records:
	///     <code>
	///     [u32 id][u32 size][u32 0][16-byte md5]
	///     </code>
	///     The md5 is content-unique, so a record is located by its old md5 (from le.idx) and the md5 and size are
	///     overwritten in place - the section headers never need parsing. Same-content resources share one md5; all
	///     matches are updated.
	/// </remarks>
	public static class RdbManifestWriter
	{
		private const int SizeOffBeforeMd5 = 8; // the u32 size sits 8 bytes before the 16-byte md5 in a record

		/// <summary>
		///     Updates <c>RDBHashIndex.bin</c> so each entry's expected md5 and size match the newly-inlined blob.
		///     Backs the manifest up to <c>RDBHashIndex.bin.faolan-bak</c> (once) before the first edit.
		/// </summary>
		/// <returns>The number of records patched.</returns>
		public static int Patch(string rdbDir, IReadOnlyList<RdbWriter.InlineResult> entries)
		{
			var path = Path.Combine(rdbDir, "RDBHashIndex.bin");
			var man = File.ReadAllBytes(path);
			if (man.Length < 8 || man[0] != (byte)'R' || man[1] != (byte)'D' || man[2] != (byte)'H' || man[3] != (byte)'I')
				throw new InvalidDataException("not an RDHI RDBHashIndex.bin");

			var bak = path + ".faolan-bak";
			if (!File.Exists(bak))
				File.Copy(path, bak);

			// Map each target old md5's first 8 bytes to the entry, so one linear pass over the 42 MB file can spot
			// every record to patch (a random md5 prefix collision is then confirmed against the full 16 bytes).
			// O(entries) memory, O(fileSize) time - no 42M-entry index.
			var byPrefix = new Dictionary<ulong, List<RdbWriter.InlineResult>>();
			foreach (var e in entries)
			{
				if (e.OldMd5.Length < 16)
					continue;

				var prefix = e.OldMd5.ReadU64Le(0);
				if (!byPrefix.TryGetValue(prefix, out var list))
				{
					list = [];
					byPrefix[prefix] = list;
				}

				list.Add(e);
			}

			var patched = 0;
			for (var pos = 0; pos + 16 <= man.Length; pos++)
			{
				var prefix = man.ReadU64Le(pos);
				if (!byPrefix.TryGetValue(prefix, out var candidates))
					continue;

				foreach (var e in candidates)
				{
					if (!man.AsSpan(pos, 16).SequenceEqual(e.OldMd5))
						continue;

					e.NewMd5.CopyTo(man.AsSpan(pos, 16));
					if (pos - SizeOffBeforeMd5 >= 0)
						man.WriteU32Le(pos - SizeOffBeforeMd5, e.Size);
					patched++;
					break;
				}
			}

			var tmp = path + ".faolan-tmp";
			File.WriteAllBytes(tmp, man);
			File.Move(tmp, path, true);
			return patched;
		}
	}
}
