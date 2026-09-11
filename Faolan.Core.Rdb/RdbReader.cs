using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     Reads the Age of Conan client Resource DataBase.
	/// </summary>
	/// <remarks>
	///     The offset index is <c>le.idx</c> (magic IBDR, version 5 for AoC / 7 for TSW); the bulk data is in
	///     <c>NN.rdbdata</c>. Each entry maps a (type, id) to a (file, offset, size); the entry data is wrapped
	///     <c>IOz1</c> plus uncompressedSize(4) plus a zlib stream. Format:
	///     <c>docs/rdb/rdb-extraction.md</c>. Validated end-to-end against a real client (TextStrings 1030002 to TDC2
	///     strings).
	/// </remarks>
	public sealed class RdbReader : IDisposable
	{
		private static readonly HttpClient Http = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(40)
		};

		private readonly Dictionary<int, FileStream> _dataFiles = [];
		private readonly HashSet<string> _cdnMisses = [];
		private readonly List<Entry> _entries = [];

		private readonly string _rdbDir;

		/// <summary>
		///     The mirror's own route for on-demand RDB resources, set by Faolan.Web.Content
		///     (<c>/{tag}/upm/{**rest}</c> with tag <c>gameupdate</c>) and mirroring retail's own
		///     <c>aoc-eu-gameupdate.live.ageofconan.com/upm/rdb/res/&lt;md5&gt;</c>. It lives here rather than in
		///     every caller's config so that a configured base is just the mirror's address.
		/// </summary>
		private const string MirrorRoute = "/gameupdate/upm/rdb";

		/// <summary>
		///     Optional CDN source for on-demand resources (file == 0xFF): the Faolan.Web.Content mirror's base
		///     address, e.g. "http://127.0.0.1:7081". On-demand resources are not in the local NN.rdbdata files; their
		///     payload is content-addressed by the entry's 16-byte md5 at
		///     <c>&lt;base&gt;&lt;MirrorRoute&gt;/res/&lt;hex[0:2]&gt;/&lt;hex[2:3]&gt;/&lt;hex[3:]&gt;</c>. The proxy
		///     is store-and-forward, so a GET both serves and archives the blob (the real CDN must be reachable for a
		///     cold miss). Misses are negative-cached so a dead or absent resource is only attempted once per process.
		/// </summary>
		private readonly string? _cdnBaseUrl;

		/// <summary>
		///     Resolves a single resource by its (type, id) key, the form RDB cross-references use ("TYPE:ID"). Built
		///     on first use (873k entries, so linear scans per lookup would be quadratic when resolving a graph).
		/// </summary>
		private Dictionary<(uint, uint), Entry>? _byKey;
		public IReadOnlyList<Entry> Entries => _entries;
		/// <summary>
		///     Every (type, id) the index states more than once, with how many entries carry it - computed at parse,
		///     so the answer costs nothing to ask and is the same whether or not anything ever calls
		///     <see cref="TryGet" />. Empty on this client; a non-empty list means an override layer is present and
		///     <see cref="TryGet" />'s last-wins rule is live. Kept as data rather than a log line because this
		///     project has no logger - the caller that has one prints it (<c>--only rdb-inventory</c> does).
		/// </summary>
		// (`--only rdb-inventory` does).
		private readonly List<(uint Type, uint Id, int Count)> _duplicateKeys = [];
		public IReadOnlyList<(uint Type, uint Id, int Count)> DuplicateKeys => _duplicateKeys;

		public int Count => _entries.Count;

		public RdbReader(string rdbDir, string? cdnBaseUrl = null)
		{
			_rdbDir = rdbDir;
			// A base that already carries the route is accepted rather than doubled, so a stale config keeps working.
			var trimmed = cdnBaseUrl?.TrimEnd('/');
			_cdnBaseUrl = string.IsNullOrWhiteSpace(trimmed)
				? null
				: trimmed.EndsWith(MirrorRoute, StringComparison.OrdinalIgnoreCase)
					? trimmed
					: trimmed + MirrorRoute;
			ParseIndex(Path.Combine(rdbDir, "le.idx"));
		}

		public void Dispose()
		{
			foreach (var fs in _dataFiles.Values)
				fs.Dispose();
			_dataFiles.Clear();
		}
		/// <summary>
		///     True if the entry's payload is likely readable - a local rdbdata entry, or a CDN entry with a
		///     configured source that has not already missed. A CDN hit is optimistic: the actual fetch may still 404
		///     (then it is negative-cached and <see cref="ReadRaw" /> returns empty), which the asset callers already
		///     treat as missing.
		/// </summary>
		// negative-cached and ReadRaw returns empty), which the asset callers already treat as "missing".
		public bool CanRead(Entry e)
		{
			return e.IsLocal || (_cdnBaseUrl != null && e.Hash.Length >= 16 && !_cdnMisses.Contains(Convert.ToHexStringLower(e.Hash)));
		}
		/// <summary>
		///     The proxy URL for a CDN (file == 0xFF) entry, or null if no source is configured or the hash is absent.
		/// </summary>
		// The proxy URL for a CDN (file == 0xFF) entry, or null if no source is configured / the hash is absent.
		public string? CdnUrl(Entry e)
		{
			if (_cdnBaseUrl == null || e.Hash.Length < 16)
				return null;

			var hex = Convert.ToHexStringLower(e.Hash);
			return $"{_cdnBaseUrl}/res/{hex[..2]}/{hex.Substring(2, 1)}/{hex[3..]}";
		}

		public IEnumerable<Entry> EntriesOfType(uint type)
		{
			return _entries.Where(e => e.Type == type);
		}
		/// <summary>
		///     Reads an entry's payload, unwrapping the IOz1 plus zlib container. Returns the decompressed bytes
		///     (which begin with the 12-byte RDB-Id data header), or the raw bytes if it is not an IOz1/zlib entry.
		/// </summary>
		// begin with the 12-byte RDB-Id data header), or the raw bytes if it isn't an IOz1/zlib entry.
		public byte[] ReadDecompressed(Entry e)
		{
			return UnwrapIOz1(ReadRaw(e));
		}
		/// <summary>
		///     Reads an RDBH-encrypted entry (only item templates, RDB types 1000020/1000620, are RDBH). The 20-byte
		///     header is <c>"RDBH" + ver=1 + flags=1 + plainLen + reserved</c>; the ciphertext is TEA-ECB encrypted
		///     with the item's per-template key (the <c>ProtoDecryptionKey</c>, <c>Item.Data1..4</c>). Decrypts,
		///     truncates to plainLen, then unwraps the inner IOz1/zlib. If the entry is not RDBH, falls back to the
		///     plain IOz1 unwrap and the key is ignored.
		/// </summary>
		public byte[] ReadDecrypted(Entry e, ReadOnlySpan<uint> teaKey)
		{
			var raw = ReadRaw(e);
			if (!IsRdbh(raw))
				return UnwrapIOz1(raw);

			var plainLen = (int)raw.ReadU32Le(12);
			var decrypted = Tea.DecryptEcb(raw.AsSpan(20), teaKey);
			var inner = plainLen >= 0 && plainLen <= decrypted.Length
				? decrypted[..plainLen]
				: decrypted;
			return UnwrapIOz1(inner);
		}

		/// <summary>
		///     Resolves a (type, id) to its entry.
		/// </summary>
		/// <remarks>
		///     The last entry wins when a (type, id) appears more than once. That is the patch semantic - a later
		///     index entry is an override of the earlier one - and it is a decision, not the incidental result of a
		///     <c>GroupBy(...).First()</c>. This client's <c>le.idx</c> has zero duplicate keys (measured: v5,
		///     873,588 entries, 0 collisions), so the rule changes nothing today; it exists so that if a patch layer
		///     ever does add a second entry, the consumer gets the new copy rather than silently the old one.
		///     <see cref="DuplicateKeys" /> is how that stops being silent either way.
		/// </remarks>
		public bool TryGet(uint type, uint id, out Entry entry)
		{
			if (_byKey == null)
			{
				_byKey = new Dictionary<(uint, uint), Entry>(_entries.Count);
				foreach (var e in _entries)
					_byKey[(e.Type, e.Id)] = e;
			}

			return _byKey.TryGetValue((type, id), out entry);
		}

		/// <summary>Counts entries per type, for the type-inventory phase.</summary>
		public IEnumerable<(uint Type, int Count)> TypeCounts()
		{
			return _entries.GroupBy(e => e.Type).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2);
		}
		/// <summary>
		///     Synchronously GETs a CDN entry's blob through the mirror proxy. Returns empty (and negative-caches the
		///     hash) on any failure - no source configured, network error, or a 404 (resource not on the CDN, or CDN
		///     unreachable).
		/// </summary>
		// any failure - no source configured, network error, or a 404 (resource not on the CDN / CDN unreachable).
		private byte[] FetchCdn(Entry e)
		{
			var url = CdnUrl(e);
			if (url == null)
				return [];

			try
			{
				using var resp = Http.Send(new HttpRequestMessage(HttpMethod.Get, url));
				if (!resp.IsSuccessStatusCode)
				{
					_cdnMisses.Add(Convert.ToHexStringLower(e.Hash));
					return [];
				}

				using var ms = new MemoryStream();
				resp.Content.ReadAsStream().CopyTo(ms);
				return ms.ToArray();
			}
			catch
			{
				_cdnMisses.Add(Convert.ToHexStringLower(e.Hash));
				return [];
			}
		}

		private FileStream GetDataFile(int file)
		{
			if (_dataFiles.TryGetValue(file, out var fs))
				return fs;

			fs = File.OpenRead(Path.Combine(_rdbDir, $"{file:D2}.rdbdata"));
			_dataFiles[file] = fs;
			return fs;
		}

		private void ParseIndex(string path)
		{
			var b = File.ReadAllBytes(path);
			if (b.Length < 28 || b[0] != (byte)'I' || b[1] != (byte)'B' || b[2] != (byte)'D' || b[3] != (byte)'R')
				throw new InvalidDataException($"le.idx magic mismatch (expected IBDR) at {path}");

			var version = b.ReadU32Le(4);
			var numEntries = (int)b.ReadU32Le(24);

			// Detail record: v5 is 11 u32 (44 B), v7 is 28 B. Both put file at byte 0 (low byte), offset at byte 4,
			// size at byte 8 - only the stride differs.
			var detailStride = version == 5
				? 44
				: 28;

			var indexOff = 28;
			var detailOff = indexOff + numEntries * 8;

			_entries.Capacity = numEntries;
			for (var i = 0; i < numEntries; i++)
			{
				var type = b.ReadU32Le(indexOff + i * 8);
				var id = b.ReadU32Le(indexOff + i * 8 + 4);

				var d = detailOff + i * detailStride;
				var file = b[d] & 0xff;
				var offset = b.ReadU32Le(d + 4);
				var size = b.ReadU32Le(d + 8);

				var hash = b.AsSpan(d + 28, 16).ToArray(); // content md5, the CDN path hash for file==0xFF resources
				_entries.Add(new Entry(type, id, file, offset, size, hash));
			}

			CensusDuplicateKeys();
		}

		/// <summary>
		///     One pass over the parsed entries recording any (type, id) stated more than once.
		/// </summary>
		/// <remarks>
		///     A key index that resolved a collision silently would serve a patch layer's entry stale, and the symptom
		///     would read as wrong content rather than a wrong pick. Counting here means "does this client's index
		///     collide?" is answered by the reader itself instead of by a hand-written index parser.
		/// </remarks>
		private void CensusDuplicateKeys()
		{
			var counts = new Dictionary<(uint, uint), int>(_entries.Count);
			foreach (var e in _entries)
				counts[(e.Type, e.Id)] = counts.GetValueOrDefault((e.Type, e.Id)) + 1;

			foreach (var (key, n) in counts)
			{
				if (n > 1)
					_duplicateKeys.Add((key.Item1, key.Item2, n));
			}

			_duplicateKeys.Sort((x, y) => x.Type != y.Type
				? x.Type.CompareTo(y.Type)
				: x.Id.CompareTo(y.Id));
		}

		private byte[] ReadRaw(Entry e)
		{
			// CDN (on-demand) entry: pull the content-addressed blob through the mirror proxy instead of a local file.
			if (!e.IsLocal)
				return FetchCdn(e);

			var fs = GetDataFile(e.File);
			fs.Seek(e.Offset, SeekOrigin.Begin);
			var buf = new byte[e.Size];
			var read = 0;
			while (read < buf.Length)
			{
				var n = fs.Read(buf, read, buf.Length - read);
				if (n <= 0)
					break;

				read += n;
			}

			return buf;
		}

		/// <summary>
		///     Whether this entry is TEA-encrypted (an <c>RDBH</c> wrapper) and so needs a per-template key, rather
		///     than being readable with none.
		/// </summary>
		/// <remarks>
		///     Asked of a whole type it is a census the record's own header cannot give: the RDBH header carries no
		///     category tag, so the split is the closest thing to a kind the bytes state. It agrees with the LDB name
		///     category, which states the kind from outside.
		/// </remarks>
		public bool IsEncrypted(Entry e)
		{
			return IsRdbh(ReadRaw(e));
		}

		public static bool IsRdbh(ReadOnlySpan<byte> raw)
		{
			return raw.Length >= 20 && raw[0] == (byte)'R' && raw[1] == (byte)'D' && raw[2] == (byte)'B' && raw[3] == (byte)'H';
		}

		/// <summary>
		///     The entry's bytes exactly as the pack file holds them - still RDBH-wrapped and still encrypted when it
		///     is an encrypted record. Every ordinary caller wants <see cref="ReadDecompressed" /> or
		///     <see cref="ReadDecrypted" /> instead; this exists for the one job that needs the ciphertext rather than
		///     the content: hunting a record's TEA key by trial decryption (<c>FeatKeyScanner</c>).
		/// </summary>
		public byte[] ReadRawEntry(Entry e)
		{
			return ReadRaw(e);
		}

		public static byte[] UnwrapIOz1Public(byte[] raw)
		{
			return UnwrapIOz1(raw);
		}

		private static byte[] Inflate(byte[] data, int offset, int expectedSize)
		{
			using var input = new MemoryStream(data, offset, data.Length - offset);
			using var zlib = new ZLibStream(input, CompressionMode.Decompress);
			using var output = expectedSize > 0
				? new MemoryStream(expectedSize)
				: new MemoryStream();
			zlib.CopyTo(output);
			return output.ToArray();
		}

		private static byte[] UnwrapIOz1(byte[] raw)
		{
			if (raw.Length >= 8 && raw[0] == (byte)'I' && raw[1] == (byte)'O' && raw[2] == (byte)'z' && raw[3] == (byte)'1')
			{
				var uncompressed = raw.ReadU32Le(4);
				try
				{
					return Inflate(raw, 8, (int)uncompressed);
				}
				catch
				{
					return raw; // not a clean zlib stream - hand back raw so the caller can decide
				}
			}

			return raw;
		}

		public readonly record struct Entry(uint Type, uint Id, int File, uint Offset, uint Size, byte[] Hash)
		{
			// On-demand resources live on the CDN, not the local NN.rdbdata files (sentinel file 0xFF).
			public bool IsLocal => File != 0xff;
		}
	}
}
