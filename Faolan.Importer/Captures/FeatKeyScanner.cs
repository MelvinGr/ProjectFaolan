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
	/// <summary>
	///     Hunts the per-template TEA keys for the encrypted feat records.
	/// </summary>
	/// <remarks>
	///     RDB type 1000020 holds items and feats together; most records are `RDBH` TEA-encrypted and the key is
	///     per-template. For items the key rides the wire - the server streams it as the item's ProtoDecryptionKey,
	///     stored in Item.Key - so item templates decrypt. For feats no such carrier is known, which is why the catalog
	///     covers only the plaintext records. The hypothesis this tests: the feat key rides the unlock/train event, the
	///     way an item's key arrives when the item is acquired rather than on every login - so login captures of
	///     already-specced characters are exactly where it would not be.
	///     Method: treat each candidate quad as a key and TEA-decrypt the target record's first block only. That block
	///     is decisive and cheap: a correct key yields the container magic (or the bare template head) and a wrong one
	///     effectively never will. One 8-byte decrypt per candidate rather than a whole record.
	///     Any negative result from a scanner like this is worthless until the scan has been run with a positive
	///     control - records whose keys are already held from captured grants - and found them.
	/// </remarks>
	public static class FeatKeyScanner
	{
		// What a correct key yields is the `IOz1` container magic, not the template: `RdbReader.ReadDecrypted` decrypts
		// and then calls `UnwrapIOz1`, so the plaintext under the cipher is an IOz1-compressed container and the type
		// word only appears after inflating it. `IOz1` read as a little-endian u32 is 0x317A4F49; the length word
		// beside it is the uncompressed size, which bounds rather than predicts - 32 bits of magic plus a plausible
		// size is far more than enough over ~10^9 windows.
		private const uint IOz1Magic = 0x317A4F49;

		// A decompressed 1000020 record is a template, not a file: the largest in the catalog is a few KB. The bound
		// only has to be tight enough that a random 32-bit word rarely passes it.
		private const uint MaxPlainLength = 1 << 20;

		// The bare oracle: a record that is not IOz1-wrapped decrypts straight to [u32 type 1000020][u32 id]
		// (UnwrapIOz1 hands back raw when the magic is absent), so both forms are legitimate hits.
		private const uint TemplateTypeWord = 1000020;

		// The key quad's dominant carrier is protobuf, where it is not 16 contiguous bytes at all. A `ProtoAcgItem`
		// writes the four words as four `fixed32` fields, so a tag byte sits between each one:
		//
		//     0d <k1 LE>  15 <k2 LE>  1d <k3 LE>  25 <k4 LE>          (fields 1-4, wire type 5, 20 bytes)
		//
		// A contiguous-window scan can never see that, whatever byte order it tries; only the minority of keys ride
		// the other carrier, the raw fixed-width inventory/snapshot form (`<k1><k2><k3><k4>` big-endian, 16 bytes).
		// The tag signature is a 4-byte test before any arithmetic, so it costs almost nothing per window and is far
		// cheaper than a trial decrypt - the tags locate the quad structurally instead of guessing it.
		private const byte Fixed32Field1 = 0x0D, Fixed32Field2 = 0x15, Fixed32Field3 = 0x1D, Fixed32Field4 = 0x25;

		// One target: the encrypted record being opened, and the id its first block must reveal.
		public readonly record struct Target(uint TemplateId, byte[] FirstBlock);

		public readonly record struct Hit(uint TemplateId, uint[] Key, string Source, int Offset, string WordOrder);

		// A candidate quad plus where it was found. The provenance is the point: a key we do not hold, found in a
		// message we can name, says which parser is missing - which is the actionable half of a hit.
		public readonly record struct Candidate(uint[] Key, string Source, int Offset, string Carrier, string Message);

		/// <summary>
		///     Scans <paramref name="captureFiles" /> for a key that opens any of <paramref name="targets" />.
		/// </summary>
		/// <remarks>
		///     Windows are byte-aligned, not word-aligned: a key need not sit on a 4-byte boundary within a packet body,
		///     and assuming it does is the kind of shortcut that makes a negative result meaningless. Three candidate
		///     layouts are tried per window - the quad as 16 contiguous bytes in either word order, and the protobuf
		///     `fixed32` form where tag bytes separate the words. Both word orders are required: the protocol is
		///     big-endian and `AddSpecialAbilityCodec` reads a wire key as four `ReadU32Be`, while the RDB record stores
		///     little-endian.
		/// </remarks>
		public static List<Hit> Scan(IEnumerable<string> captureFiles, IReadOnlyList<Target> targets, Action<string>? log = null)
		{
			var hits = new ConcurrentBag<Hit>();
			var files = captureFiles.ToList();
			var scanned = 0L;

			foreach (var path in files)
			{
				var bodies = ReadBodies(path);
				long windows = 0;
				Parallel.ForEach(bodies, entry =>
				{
					var body = entry.Body;
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

						// The protobuf-carried quad: same four words, tag-separated, so it lives in a 20-byte window
						// this loop would otherwise walk straight past.
						if (i + 20 > body.Length || body[i] != Fixed32Field1 || body[i + 5] != Fixed32Field2 || body[i + 10] != Fixed32Field3 || body[i + 15] != Fixed32Field4)
							continue;

						Span<uint> proto =
						[
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 1)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 6)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 11)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 16))
						];

						foreach (var target in targets)
							TryKey(proto, "proto", target, path, i, hits);
					}

					System.Threading.Interlocked.Add(ref windows, local);
				});

				scanned += windows;
				log?.Invoke($"  {Path.GetFileName(path)}: {windows:N0} windows (running total {scanned:N0}){(hits.IsEmpty ? "" : $"  ** {hits.Count} HIT(S) **")}");
			}

			return [.. hits];
		}

		// One candidate key against one target's first block. The block itself stays little-endian: that is the RDB
		// record's own storage, not the wire's, and it is the order `RdbReader`'s decryption uses.
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

		/// <summary>
		///     The inverted scan: instead of "every window against a few targets", harvest the corpus's candidate quads
		///     structurally and test each against every encrypted record. That turns a sampled question into an
		///     exhaustive one, and needs no guess about which records the corpus might carry.
		/// </summary>
		/// <remarks>
		///     Two anchors, one per carrier, both of which locate a quad without knowing its value: protobuf - four
		///     `fixed32` fields, `0d <k1> 15 <k2> 1d <k3> 25 <k4>` (this tag run is generic: any four-float message
		///     matches it, so most candidates are quaternions and the set is a candidate list, not a key list - that
		///     costs a trial decrypt each and nothing else); raw fixed-width - the inventory/snapshot form writes the
		///     quad twice back to back, so 16 bytes repeated immediately is the anchor (`<quad><quad>`), big-endian.
		/// </remarks>
		public static List<Candidate> HarvestCandidates(IEnumerable<string> captureFiles)
		{
			var seen = new HashSet<(uint, uint, uint, uint)>();
			var list = new List<Candidate>();
			foreach (var path in captureFiles)
			{
				var file = Path.GetFileName(path);
				foreach (var (body, message) in ReadBodies(path))
				{
					for (var i = 0; i + 20 <= body.Length; i++)
					{
						if (body[i] != Fixed32Field1 || body[i + 5] != Fixed32Field2 || body[i + 10] != Fixed32Field3 || body[i + 15] != Fixed32Field4)
							continue;

						Add(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 1)), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 6)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 11)), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 16)),
							file, i, "protobuf", message);
					}

					for (var i = 0; i + 32 <= body.Length; i++)
					{
						if (body.AsSpan(i, 16).SequenceEqual(body.AsSpan(i + 16, 16)))
							Add(BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i)), BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 4)),
								BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 8)), BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 12)),
								file, i, "raw-doubled", message);
					}
				}
			}

			return list;

			void Add(uint a, uint b, uint c, uint d, string file, int offset, string carrier, string message)
			{
				if ((a | b | c | d) != 0 && seen.Add((a, b, c, d)))
					list.Add(new Candidate([a, b, c, d], file, offset, carrier, message));
			}
		}

		// Tests every candidate against every target. Parallel over candidates; the targets are a flat array of
		// 8-byte first blocks, so the inner loop is cache-friendly.
		public static List<Hit> ScanCandidates(IReadOnlyList<Candidate> candidates, IReadOnlyList<Target> targets, Action<string>? log = null)
		{
			var hits = new ConcurrentBag<Hit>();
			var done = 0;
			Parallel.For(0, candidates.Count, ci =>
			{
				Span<uint> key = stackalloc uint[4];
				var c = candidates[ci];
				key[0] = c.Key[0];
				key[1] = c.Key[1];
				key[2] = c.Key[2];
				key[3] = c.Key[3];
				foreach (var target in targets)
					TryKey(key, $"{c.Carrier}/{c.Message}", target, c.Source, c.Offset, hits);

				var n = System.Threading.Interlocked.Increment(ref done);
				if (n % 500 == 0)
					log?.Invoke($"  {n:N0}/{candidates.Count:N0} candidates tested ({hits.Count} hit(s))");
			});

			return [.. hits];
		}

		// Every packet body in a tshark dump, as raw bytes, plus a short name for the message it is - the opcode, and
		// the IIR sub-opcode when it carries one - so a hit can say which shape the key rode in, which is what turns
		// "there is a key we do not hold" into "we are not parsing X". Deliberately not filtered to the Conan
		// protocol: a key could ride any stream.
		private static List<(byte[] Body, string Message)> ReadBodies(string path)
		{
			var list = new List<(byte[], string)>();
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
						var body = Convert.FromHexString(hex.GetString()!);
						var opcode = p.TryGetProperty("Opcode", out var op) && op.ValueKind == JsonValueKind.String ? op.GetString() : null;
						var iir = p.TryGetProperty("Iir", out var ir) && ir.ValueKind == JsonValueKind.String ? ir.GetString() : null;
						var dir = p.TryGetProperty("Direction", out var dr) && dr.ValueKind == JsonValueKind.String ? dr.GetString() : "";

						// The opcode alone is not the message. `0x2NNN` is the wire rendering of the method instance
						// and a method index is per interface, so two different methods can print the same number
						// (the tradepost search result and the wallet update share `0x2005`). The receiver class is
						// what disambiguates it, so it is part of the name.
						var recv = p.TryGetProperty("ReceiverClass", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString() : null;
						var name = string.IsNullOrEmpty(iir) ? $"{opcode ?? "?"}->{recv ?? "?"}" : $"{opcode}:{iir}";
						list.Add((body, $"{dir} {name}".Trim()));
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
