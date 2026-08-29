using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Crypt;

namespace Faolan.Importer.Captures
{
	// Hunts the per-template TEA keys for the ENCRYPTED feat records (Gitea #251).
	//
	// RDB type 1000020 holds items and feats together; ~79% are `RDBH` TEA-encrypted and the key is per-template. For
	// ITEMS the key rides the wire - the server streams it as the item's ProtoDecryptionKey and we store it in
	// Item.Key - so item templates decrypt. For FEATS no such carrier is known, which is why the catalog covers only
	// the plaintext ~21%.
	//
	// The hypothesis this tests (#251's own plan): the feat key rides the UNLOCK/TRAIN event, the way an item's key
	// arrives when the item is acquired rather than on every login. An earlier attempt scanned only already-specced
	// characters' LOGIN captures - i.e. exactly where the plan predicted the key would NOT be - and found nothing.
	//
	// Method: treat every 16-byte window in every captured packet body as a candidate key and TEA-decrypt the target
	// record's FIRST BLOCK ONLY. That block is decisive and cheap: a plaintext feat record opens
	// [u32 type 1000020][u32 id], so a correct key yields exactly those 8 bytes and a wrong one effectively
	// never will. One 8-byte decrypt per candidate rather than a whole record.
	public static class FeatKeyScanner
	{
		// ⚠️⚠️ **What a correct key actually yields is `IOz1`, not the template.** This scanner shipped asserting that
		// the first decrypted block is `[u32 1000020][u32 id]` - the template's own type word and id - and that is
		// simply not what comes out. `RdbReader.ReadDecrypted` decrypts and THEN calls `UnwrapIOz1`: the plaintext
		// under the cipher is an IOz1-compressed container, and the type word only appears after inflating it. So the
		// old oracle could never fire, for any key, which is the second half of why this scanner's negative result
		// was worthless (the first was reading candidates little-endian in a big-endian protocol).
		//
		// Both halves were caught the same way: by running the scan with a POSITIVE CONTROL (#895) - two ability
		// records whose keys we already hold from captured 0x2052 grants. The keys are literally present in 50 of the
		// dumps, and the scan reported NOT FOUND for them.
		//
		// The oracle is now the container magic plus a sane declared length. `IOz1` read as a little-endian u32 is
		// 0x317A4F49; the length word beside it is the uncompressed size, which bounds rather than predicts - 32 bits
		// of magic plus a plausible size is far more than enough over ~10^9 windows.
		private const uint IOz1Magic = 0x317A4F49;

		// A decompressed 1000020 record is a template, not a file: the largest in the catalog is a few KB. The bound
		// only has to be tight enough that a random 32-bit word rarely passes it.
		private const uint MaxPlainLength = 1 << 20;

		// The historical oracle, kept because a record that is NOT IOz1-wrapped decrypts straight to the template
		// (UnwrapIOz1 hands back raw when the magic is absent), so both forms are legitimate hits.
		private const uint TemplateTypeWord = 1000020;

		// One target: the encrypted record we are trying to open, and the id its first block must reveal.
		public readonly record struct Target(uint TemplateId, byte[] FirstBlock);

		public readonly record struct Hit(uint TemplateId, uint[] Key, string Source, int Offset, string WordOrder);

		// Scans `captureFiles` for a key that opens any of `targets`. Windows are BYTE-aligned, not word-aligned: a
		// key need not sit on a 4-byte boundary within a packet body, and assuming it does is the kind of shortcut
		// that makes a negative result meaningless.
		//
		// ⚠️⚠️ **Each window is tried in BOTH word orders, and that is not belt-and-braces - it is the bug this
		// scanner shipped with.** It read every candidate as four LITTLE-endian words, while this protocol is
		// BIG-endian and the one reader that harvests a key off the wire - `AddSpecialAbilityCodec`, the 0x2052
		// ability grant - takes it as four `ReadU32Be`. So a key that demonstrably rode the wire could never be
		// reconstructed here, and the scan's "NO KEY FOUND" was a statement about the scanner rather than about the
		// traffic. Caught by running it with a POSITIVE CONTROL (#895): two ability records whose keys we already
		// hold, harvested from captured grants, came back not-found alongside the ones being hunted.
		//
		// The lesson generalises past this file: a search whose only result is a negative is worth nothing until it
		// has been shown to find something that is known to be there.
		public static List<Hit> Scan(IEnumerable<string> captureFiles, IReadOnlyList<Target> targets, Action<string>? log = null)
		{
			var hits = new ConcurrentBag<Hit>();
			var files = captureFiles.ToList();
			var scanned = 0L;

			foreach (var path in files)
			{
				var bodies = ReadBodies(path);
				long windows = 0;
				Parallel.ForEach(bodies, body =>
				{
					if (body.Length < 16)
						return;

					var local = 0L;
					for (var i = 0; i + 16 <= body.Length; i++)
					{
						local++;
						Span<uint> le =
						[
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 4)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 8)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 12))
						];
						Span<uint> be =
						[
							BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i)),
							BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 4)),
							BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 8)),
							BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 12))
						];

						foreach (var target in targets)
						{
							TryKey(le, "le", target, path, i, hits);
							TryKey(be, "be", target, path, i, hits);
						}
					}

					System.Threading.Interlocked.Add(ref windows, local);
				});

				scanned += windows;
				log?.Invoke($"  {Path.GetFileName(path)}: {windows:N0} windows (running total {scanned:N0}){(hits.IsEmpty ? "" : $"  ** {hits.Count} HIT(S) **")}");
			}

			return [.. hits];
		}

		// One candidate key against one target's first block. The block itself stays LITTLE-endian: that is the RDB
		// record's own storage, not the wire's, and it is the order `RdbReader`'s working decryption already uses.
		private static void TryKey(Span<uint> key, string order, Target target, string path, int offset, ConcurrentBag<Hit> hits)
		{
			var v0 = BinaryPrimitives.ReadUInt32LittleEndian(target.FirstBlock);
			var v1 = BinaryPrimitives.ReadUInt32LittleEndian(target.FirstBlock.AsSpan(4));
			Tea.DecryptBlock(ref v0, ref v1, key);

			var wrapped = v0 == IOz1Magic && v1 > 0 && v1 <= MaxPlainLength;
			var bare = v0 == TemplateTypeWord && v1 == target.TemplateId;
			if (!wrapped && !bare)
				return;

			hits.Add(new Hit(target.TemplateId, [key[0], key[1], key[2], key[3]], Path.GetFileName(path), offset, order + (wrapped ? "/IOz1" : "/bare")));
		}

		// Every packet body in a tshark dump, as raw bytes. Deliberately NOT filtered to the Conan protocol: a key
		// could ride any stream, and filtering is how the previous sweep could have missed it.
		private static List<byte[]> ReadBodies(string path)
		{
			var list = new List<byte[]>();
			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(File.ReadAllText(path));
			}
			catch
			{
				return list;
			}

			using (doc)
			{
				if (!doc.RootElement.TryGetProperty("Packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
					return list;

				foreach (var p in packets.EnumerateArray())
				{
					if (!p.TryGetProperty("BodyHex", out var hex) || hex.ValueKind != JsonValueKind.String)
						continue;

					try
					{
						list.Add(Convert.FromHexString(hex.GetString()!));
					}
					catch
					{
						// a body we cannot parse is one we cannot scan - skip it rather than abort the file
					}
				}
			}

			return list;
		}
	}
}
