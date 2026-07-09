using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using Faolan.Core.Crypt;

namespace Faolan.Core.Rdb
{
	// Reads the Age of Conan client Resource DataBase. The offset index is `le.idx` (magic IBDR, version 5 for
	// AoC / 7 for TSW); the bulk data is in `NN.rdbdata`. Each entry maps a (type, id) to a (file, offset, size);
	// the entry data is wrapped `IOz1` + uncompressedSize(4) + a zlib stream. Format: docs/rdb/rdb-extraction-plan.md.
	// Validated end-to-end against a real client (TextStrings 1030002 -> TDC2 strings).
	public sealed class RdbReader : IDisposable
	{
		private static readonly HttpClient Http = new()
		{
			Timeout = TimeSpan.FromSeconds(40)
		};

		private readonly Dictionary<int, FileStream> _dataFiles = new();
		private readonly HashSet<string> _cdnMisses = [];
		private readonly List<Entry> _entries = [];

		private readonly string _rdbDir;

		// Optional CDN source for on-demand resources (file == 0xFF): the Faolan.Web.Content proxy base, e.g.
		// "http://127.0.0.1:7080/gameupdate/upm/rdb". On-demand resources aren't in the local NN.rdbdata files; their
		// payload is content-addressed by the entry's 16-byte md5 at <base>/res/<hex[0:2]>/<hex[2:3]>/<hex[3:]>. The
		// proxy is store-and-forward, so a GET both serves and archives the blob (the real CDN must be reachable for a
		// cold miss). Misses are negative-cached so a dead/absent resource is only attempted once per process.
		private readonly string? _cdnBaseUrl;

		// Resolve a single resource by its (type, id) key, the form RDB cross-references use ("TYPE:ID"). Builds a
		// lookup on first use (873k entries, so linear scans per lookup would be quadratic when resolving a graph).
		private Dictionary<(uint, uint), Entry>? _byKey;

		public int Count => _entries.Count;
		public IReadOnlyList<Entry> Entries => _entries;

		public RdbReader(string rdbDir, string? cdnBaseUrl = null)
		{
			_rdbDir = rdbDir;
			_cdnBaseUrl = string.IsNullOrWhiteSpace(cdnBaseUrl)
				? null
				: cdnBaseUrl.TrimEnd('/');
			ParseIndex(Path.Combine(rdbDir, "le.idx"));
		}

		public void Dispose()
		{
			foreach (var fs in _dataFiles.Values)
				fs.Dispose();
			_dataFiles.Clear();
		}

		// True if the entry's payload is (likely) readable - a local rdbdata entry, or a CDN entry with a configured
		// source that hasn't already missed. A CDN "hit" is optimistic: the actual fetch may still 404 (then it's
		// negative-cached and ReadRaw returns empty), which the asset callers already treat as "missing".
		public bool CanRead(Entry e)
		{
			return e.IsLocal || (_cdnBaseUrl != null && e.Hash.Length >= 16 && !_cdnMisses.Contains(Convert.ToHexStringLower(e.Hash)));
		}

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

		// Reads an entry's payload, unwrapping the IOz1 + zlib container. Returns the decompressed bytes (which
		// begin with the 12-byte RDB-Id data header), or the raw bytes if it isn't an IOz1/zlib entry.
		public byte[] ReadDecompressed(Entry e)
		{
			return UnwrapIOz1(ReadRaw(e));
		}

		// Reads an RDBH-encrypted entry (only item-templates, RDB types 1000020/1000620, are RDBH). The 20-byte
		// header is `"RDBH" + ver=1 + flags=1 + plainLen + reserved`; the ciphertext is TEA-ECB encrypted with the
		// item's per-template key (= ProtoDecryptionKey = Item.Data1..4). Decrypts, truncates to plainLen, then
		// unwraps the inner IOz1/zlib. If the entry isn't RDBH, falls back to the plain IOz1 unwrap (key ignored).
		public byte[] ReadDecrypted(Entry e, ReadOnlySpan<uint> teaKey)
		{
			var raw = ReadRaw(e);
			if (!IsRdbh(raw))
				return UnwrapIOz1(raw);

			var plainLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4));
			var decrypted = Tea.DecryptEcb(raw.AsSpan(20), teaKey);
			var inner = plainLen >= 0 && plainLen <= decrypted.Length
				? decrypted[..plainLen]
				: decrypted;
			return UnwrapIOz1(inner);
		}

		public bool TryGet(uint type, uint id, out Entry entry)
		{
			_byKey ??= _entries.GroupBy(e => (e.Type, e.Id)).ToDictionary(g => g.Key, g => g.First());
			return _byKey.TryGetValue((type, id), out entry);
		}

		// Counts entries per type (for the type-inventory phase).
		public IEnumerable<(uint Type, int Count)> TypeCounts()
		{
			return _entries.GroupBy(e => e.Type).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2);
		}

		// Synchronously GET a CDN entry's blob through the mirror proxy. Returns empty (and negative-caches the hash) on
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

			var version = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4, 4));
			var numEntries = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24, 4));

			// Detail record: v5 = 11 u32 (44 B), v7 = 28 B. Both put file at byte 0 (low byte), offset at byte 4,
			// size at byte 8 - only the stride differs.
			var detailStride = version == 5
				? 44
				: 28;

			var indexOff = 28;
			var detailOff = indexOff + numEntries * 8;

			_entries.Capacity = numEntries;
			for (var i = 0; i < numEntries; i++)
			{
				var type = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(indexOff + i * 8, 4));
				var id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(indexOff + i * 8 + 4, 4));

				var d = detailOff + i * detailStride;
				var file = b[d] & 0xff; // unk0 & 0xff
				var offset = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(d + 4, 4));
				var size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(d + 8, 4));

				var hash = b.AsSpan(d + 28, 16).ToArray(); // 16-byte content hash = CDN path md5 for file==0xFF resources
				_entries.Add(new Entry(type, id, file, offset, size, hash));
			}
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

		public static bool IsRdbh(ReadOnlySpan<byte> raw)
		{
			return raw.Length >= 20 && raw[0] == (byte)'R' && raw[1] == (byte)'D' && raw[2] == (byte)'B' && raw[3] == (byte)'H';
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
				var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
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
