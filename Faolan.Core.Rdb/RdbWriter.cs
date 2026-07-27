using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	// Writes into an AoC RDB (#295): INLINES on-demand (file==0xFF, CDN) resources into a local NN.rdbdata and
	// flips their le.idx detail record to point at it, making the install self-contained (no CDN dependency).
	//
	// This is exactly what the GAME CLIENT itself does when it caches a CDN resource: its le.idx writer
	// (AgeOfConan.exe FUN_01073ef0, "Failed to write new le.idx") rewrites the whole index from its in-memory
	// RDBIndex - IBDR + version 5 + the stored 16-byte MD5 written back verbatim (NOT recomputed) + the (type,id)
	// pairs + the 44-byte detail records + packs. The loader (FUN_01072430) checks only the IBDR magic + version;
	// there is NO integrity gate at load. So a hand-edited index is accepted by the client (the official ConanPatcher
	// re-validates against the manifest, but the emulator launches the game directly, bypassing it).
	//
	// Detail record (v5, 44 bytes): [byte0 = file][u32@4 = offset][u32@8 = size]...[16-byte content md5 @28]. The
	// md5 (= the CDN path hash) is left unchanged - the local read path is by (file,offset,size) and never re-hashes.
	public static class RdbWriter
	{
		private const int DetailStrideV5 = 44;
		private const int Md5Off = 28; // the 16-byte content md5 inside a v5 detail record

		// Inline the given raw blobs (the exact IOz1+zlib bytes a local entry stores, i.e. what the CDN returns) into
		// `dataFileNumber`.rdbdata under rdbDir, and patch le.idx so each (type,id) reads from there. Appends to the
		// data file (so re-runs accumulate), rewrites le.idx in place. Also rewrites each entry's 16-byte content md5
		// to md5(blob): a REPLACED (e.g. upscaled) blob has different bytes, and the client validates local data
		// against this md5 - leaving the old hash makes ResourceManager treat the entry as corrupt and re-download it
		// (#295). `dataFileNumber` must be 0..254 (255 = the CDN sentinel). Returns one InlineResult per inlined entry
		// (feed to RdbManifestWriter to make the change survive the client's startup content-validation).
		public static IReadOnlyList<InlineResult> InlineEntries(string rdbDir, byte dataFileNumber, IReadOnlyList<(uint Type, uint Id, byte[] RawBlob)> entries)
		{
			if (dataFileNumber == 0xFF)
				throw new ArgumentException("dataFileNumber 0xFF is the CDN sentinel; pick 0..254.", nameof(dataFileNumber));

			var idxPath = Path.Combine(rdbDir, "le.idx");
			var idx = File.ReadAllBytes(idxPath);
			if (idx.Length < 28 || idx[0] != (byte)'I' || idx[1] != (byte)'B' || idx[2] != (byte)'D' || idx[3] != (byte)'R')
				throw new InvalidDataException("not an IBDR le.idx");
			if (idx.ReadU32Le(4) != 5)
				throw new NotSupportedException("only le.idx v5 is supported");

			var numEntries = (int)idx.ReadU32Le(24);
			const int indexOff = 28;
			var detailOff = indexOff + numEntries * 8;

			// (type,id) -> record index, so a resource's detail record is found in O(1).
			var byKey = new Dictionary<(uint, uint), int>(numEntries);
			for (var i = 0; i < numEntries; i++)
			{
				var t = idx.ReadU32Le(indexOff + i * 8);
				var id = idx.ReadU32Le(indexOff + i * 8 + 4);
				byKey.TryAdd((t, id), i);
			}

			var dataPath = Path.Combine(rdbDir, $"{dataFileNumber:D2}.rdbdata");
			var results = new List<InlineResult>(entries.Count);
			using (var data = new FileStream(dataPath, FileMode.OpenOrCreate, FileAccess.Write))
			{
				data.Seek(0, SeekOrigin.End);
				foreach (var (type, id, blob) in entries)
				{
					if (!byKey.TryGetValue((type, id), out var i))
						continue; // no such (type,id) in this index

					var offset = data.Position;
					if (offset + blob.Length > uint.MaxValue)
						throw new InvalidOperationException($"{dataPath} would exceed 4GB; use a fresh data file number.");

					data.Write(blob, 0, blob.Length);

					var d = detailOff + i * DetailStrideV5;
					var oldMd5 = idx.AsSpan(d + Md5Off, 16).ToArray(); // manifest key (the resource's pre-edit content hash)
					var newMd5 = MD5.HashData(blob); // the edited blob's content hash
					idx[d] = dataFileNumber; // file
					idx.WriteU32Le(d + 4, (uint)offset); // offset
					idx.WriteU32Le(d + 8, (uint)blob.Length); // size
					newMd5.CopyTo(idx.AsSpan(d + Md5Off, 16)); // content md5 (client validates local data against this)
					results.Add(new InlineResult(type, id, oldMd5, newMd5, (uint)blob.Length));
				}
			}

			// Rewrite le.idx atomically (temp + move) so a crash mid-write can't corrupt the index.
			var tmp = idxPath + ".faolan-tmp";
			File.WriteAllBytes(tmp, idx);
			File.Move(tmp, idxPath, true);
			return results;
		}

		// The before/after content hashes for one inlined resource. OldMd5 is the manifest key to locate it in
		// RDBHashIndex.bin; NewMd5 + Size describe the blob we wrote (what the manifest must be updated to so the
		// client's ResourceManager stops re-downloading it). See RdbManifestWriter (#295).
		public readonly record struct InlineResult(uint Type, uint Id, byte[] OldMd5, byte[] NewMd5, uint Size);
	}
}
