using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Faolan.Importer
{
	// Reads the Age of Conan client Resource DataBase. The offset index is `le.idx` (magic IBDR, version 5 for
	// AoC / 7 for TSW); the bulk data is in `NN.rdbdata`. Each entry maps a (type, id) to a (file, offset, size);
	// the entry data is wrapped `IOz1` + uncompressedSize(4) + a zlib stream. Format: docs/rdb-extraction-plan.md.
	// Validated end-to-end against a real client (TextStrings 1030002 -> TDC2 strings).
	public sealed class RdbReader : IDisposable
	{
		private readonly Dictionary<int, FileStream> _dataFiles = new Dictionary<int, FileStream>();
		private readonly List<Entry> _entries = [];

		private readonly string _rdbDir;

		public int Count => _entries.Count;
		public IReadOnlyList<Entry> Entries => _entries;

		public RdbReader(string rdbDir)
		{
			_rdbDir = rdbDir;
			ParseIndex(Path.Combine(rdbDir, "le.idx"));
		}

		public void Dispose()
		{
			foreach (var fs in _dataFiles.Values)
				fs.Dispose();
			_dataFiles.Clear();
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

		// Counts entries per type (for the type-inventory phase).
		public IEnumerable<(uint Type, int Count)> TypeCounts()
		{
			return _entries.GroupBy(e => e.Type).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2);
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

				_entries.Add(new Entry(type, id, file, offset, size));
			}
		}

		private byte[] ReadRaw(Entry e)
		{
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

		public readonly record struct Entry(uint Type, uint Id, int File, uint Offset, uint Size);
	}
}
