using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;

using Faolan.Core.Network.Codecs.FullCharSnapshot;

using Faolan.Core.Network.Codecs.Quest;

using Faolan.Core.Network.Codecs.Spawn;

using Faolan.Core.Network.Codecs.World;

namespace Faolan.Importer.Captures
{
	// Writes the FIXTURE ARCHIVE: every byte of capture-derived data the test suite reads, in ONE committed file
	// (Faolan.Tests/Assets/fixtures.bin). It holds two kinds of entry:
	//
	//   * CORPUS - the packet bodies the whole-corpus gates walk, pulled out of the 349 MB capture repo so the suite
	//     runs from a plain clone. Regenerated from the captures.
	//   * NAMED  - the hand-carved fixtures (the former Assets/*.bin inputs and Golden/*.bin expected outputs). These
	//     CANNOT be re-derived from the captures - they were sliced by hand - so a regeneration carries them over from
	//     the existing archive rather than rebuilding them. `--report fixture-set` adds or replaces one.
	//
	// ⚠️ The corpus half is DERIVED. If a regeneration ever silently drops packets, the whole-corpus gates get weaker
	// while still passing - which is precisely the failure mode those gates exist to catch. So the manifest pins the
	// per-IIR counts and the reader ASSERTS them; a short archive fails loudly instead of quietly narrowing coverage.
	//
	// Format (all little-endian, the on-disk container only - packet bodies keep their own big-endian wire order):
	//   "FCX2"                     magic
	//   u32 manifestDeflatedLength
	//   deflate                    manifest, UTF-8 JSON:
	//                                { counts: {iir: n},
	//                                  corpus: [{ s: source, i: packetIndex, n: bytes, f: frame, o: opcode }],
	//                                  named:  [{ k: key, n: bytes }] }
	//   deflate                    the payload: corpus entries then named entries, in manifest order
	//
	// Two things about that shape are deliberate, and together they cut the file by a third at identical coverage:
	//   * The manifest is DEFLATED. It is one JSON object per packet and dwarfed the bodies otherwise (4.9 MB plain,
	//     0.27 MB deflated at 45k entries).
	//   * Corpus entries are laid out by (IIR, length), not capture order, so same-shaped bodies sit next to each other
	//     and the compressor sees the redundancy (17.4 MB -> 14.9 MB). Capture order is NOT lost - each entry carries
	//     its source and packet index, and the READER sorts back to it, because gates like "the session's first
	//     snapshot" depend on that order.
	public static class FixtureArchiveWriter
	{
		public const string Magic = "FCX2";

		// The IIR types the test suite reads. Adding a test that needs another type means adding it here and
		// regenerating - the archive deliberately freezes the set rather than guessing.
		public static readonly (uint Iir, string Name)[] WantedIirs =
		[
			(FullPlayerCharacterCodec.Opcode, "snapshot tails, protobuf root, death vitals, quest regions"),
			(SimpleCharFullUpdateCodec.Opcode, "spawn - the protobuf round-trip corpus"),
			(MoveTaskCodec.Opcode, "MoveTask"),
			(QuestFullUpdateCodec.Opcode, "QuestFullUpdate"),
			(QuestJournalCodec.Opcode, "QuestJournal"),
			(MainQuestUpdateCodec.Opcode, "QuestLogList"),
			(PlayfieldFCodec.Opcode, "map change - NpcSpawnExtractor needs it to key spawns to a map"),
			(TradeskillReplyCodec.Opcode, "the one captured recipe catalog"),
			(WorldObjectClasses.SimpleDynel, "world objects - the identity+geometry column lift, byte-compared"),
			// #617: the 54-byte APPLY payload we CONSTRUCT from Spell rows. Every captured apply is an instance of
			// the same oracle - this spell id -> the bytes retail actually sent - including spells the capturing
			// player never cast, since other players' and NPCs' casts are broadcast to them too.
			(MagicBuffCodec.Opcode, "buff apply/end - the constructed payload's whole-corpus gate")
		];

		// Reads the tshark JSON DIRECTLY rather than going through TsharkCaptureReader, because the tests consume the
		// raw `BodyHex` bytes while CapturedPacket.Bytes is the REBUILT full packet (framing included). Extracting
		// what the tests actually read keeps their logic untouched and the archive byte-identical to the corpus.
		public static void Write(string capturesDir, string outputPath)
		{
			ArgumentNullException.ThrowIfNull(capturesDir);

			var wanted = WantedIirs.Select(w => w.Iir).ToHashSet();
			var corpus = new List<CorpusEntry>();
			var counts = new Dictionary<string, int>();
			var sources = 0;

			foreach (var json in Directory.EnumerateFiles(capturesDir, "*.tshark.json").OrderBy(f => f, StringComparer.Ordinal))
			{
				using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
				if (!doc.RootElement.TryGetProperty("Packets", out var packets))
					continue;

				sources++;
				var name = Path.GetFileName(json);
				var index = 0;
				foreach (var p in packets.EnumerateArray())
				{
					// The packet INDEX must count every packet, not just kept ones: tests report provenance as
					// "<file>#<index>" and that index has to keep meaning the same thing.
					var i = index++;
					if (!p.TryGetProperty("BodyHex", out var hexEl))
						continue;

					var hex = hexEl.GetString() ?? "";
					if (hex.Length < 16)
						continue;

					var iir = uint.Parse(hex.AsSpan(8, 8), NumberStyles.HexNumber);
					if (!wanted.Contains(iir))
						continue;

					var body = Convert.FromHexString(hex);

					// Store the FULL rebuilt packet, not the bare body: the extractors take CapturedPacket and strip
					// the framing themselves. The reader derives the body back off it, so both shapes are available.
					var opcode = p.TryGetProperty("Opcode", out var opEl) && opEl.ValueKind == JsonValueKind.String
						? Convert.ToUInt32(opEl.GetString()!.Replace("0x", ""), 16)
						: opEl.ValueKind == JsonValueKind.Number
							? opEl.GetUInt32()
							: 0x2000u;
					var full = TsharkCaptureReader.Rebuild(p, body, opcode);
					if (full == null)
						continue; // no endpoints in the dump - the same packets TsharkCaptureReader itself drops

					// tshark dumps write Frame as a NUMBER in some captures and a STRING in others - handle both, or
					// every frame silently becomes 0 and frame-keyed lookups quietly find nothing.
					var frame = 0;
					if (p.TryGetProperty("Frame", out var fEl))
					{
						frame = fEl.ValueKind switch
						{
							JsonValueKind.Number                                                    => fEl.GetInt32(),
							JsonValueKind.String when int.TryParse(fEl.GetString(), out var parsed) => parsed,
							_                                                                       => 0
						};
					}

					corpus.Add(new CorpusEntry(name, i, frame, opcode, iir, full));
					var key = $"0x{iir:x8}";
					counts[key] = counts.GetValueOrDefault(key) + 1;
				}
			}

			var named = ReadNamedEntries(outputPath);
			WriteArchive(outputPath, corpus, counts, named);

			Console.WriteLine($"Fixture archive -> {outputPath}");
			Console.WriteLine($"  {corpus.Count} packet bodies from {sources} captures + {named.Count} named fixtures, " +
			                  $"{corpus.Sum(e => (long)e.Full.Length) / 1048576.0:F1} MB raw -> {new FileInfo(outputPath).Length / 1048576.0:F1} MB on disk");
			foreach (var (iir, name) in WantedIirs)
				Console.WriteLine($"    {counts.GetValueOrDefault($"0x{iir:x8}"),6}  0x{iir:X8}  {name}");
		}

		// Adds or replaces ONE named fixture, leaving the corpus untouched. This is the only way a hand-carved fixture
		// enters the archive after the loose .bin files are gone - there is nothing to re-derive it from.
		public static void SetNamed(string archivePath, string key, string sourceFile)
		{
			ArgumentNullException.ThrowIfNull(key);

			var (counts, corpus, named) = Read(archivePath);
			named[key] = File.ReadAllBytes(sourceFile);
			WriteArchive(archivePath, corpus, counts, named);
			Console.WriteLine($"{key} <- {sourceFile} ({named[key].Length} bytes); archive now {named.Count} named fixtures, {corpus.Count} corpus entries");
		}

		// Drops ONE named fixture, leaving the corpus untouched - the counterpart of SetNamed, and the only way retail
		// bytes LEAVE the archive (#555). Used when a gate stops needing the blob because its expectation became a
		// SHA-256 pin: the digest is equally binding and one-way, so the bytes no longer have to ship.
		//
		// ⚠️ A named fixture is hand-carved and cannot be re-derived from the captures, so this is not undoable from
		// inside the repo. Take the digest FIRST, and confirm the gate passes against it, before removing.
		public static void RemoveNamed(string archivePath, string key)
		{
			ArgumentNullException.ThrowIfNull(key);

			var (counts, corpus, named) = Read(archivePath);
			if (!named.Remove(key, out var dropped))
			{
				Console.WriteLine($"{key}: not in the archive; nothing removed ({named.Count} named fixtures)");
				return;
			}

			WriteArchive(archivePath, corpus, counts, named);
			Console.WriteLine($"{key} REMOVED ({dropped.Length} bytes); archive now {named.Count} named fixtures, {corpus.Count} corpus entries");
		}

		private static void WriteArchive(string outputPath, List<CorpusEntry> corpus, Dictionary<string, int> counts, SortedDictionary<string, byte[]> named)
		{
			// Lay the corpus out by (IIR, length) so same-shaped bodies compress against each other. The reader sorts
			// back to capture order, so this is purely a container concern.
			var ordered = corpus.OrderBy(e => e.Iir).ThenBy(e => e.Full.Length).ThenBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Index).ToList();

			var manifest = JsonSerializer.SerializeToUtf8Bytes(new
			{
				counts,
				corpus = ordered.Select(e => new
				{
					s = e.Source,
					i = e.Index,
					n = e.Full.Length,
					f = e.Frame,
					o = e.Opcode
				}),
				named = named.Select(kv => new
				{
					k = kv.Key,
					n = kv.Value.Length
				})
			});

			using var output = File.Create(outputPath);
			output.Write(Encoding.ASCII.GetBytes(Magic));

			using var manifestDeflated = new MemoryStream();
			using (var deflate = new DeflateStream(manifestDeflated, CompressionLevel.SmallestSize, true))
				deflate.Write(manifest);

			Span<byte> len = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)manifestDeflated.Length);
			output.Write(len);
			manifestDeflated.Position = 0;
			manifestDeflated.CopyTo(output);

			using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, true))
			{
				foreach (var e in ordered)
					deflate.Write(e.Full);
				foreach (var body in named.Values)
					deflate.Write(body);
			}
		}

		// The named half of an existing archive, or - on the FIRST pack, when there is no archive yet - the loose
		// Assets/*.bin + Golden/*.bin files beside it. Seeding this way is what lets the loose files be deleted in the
		// same commit that introduces the archive.
		private static SortedDictionary<string, byte[]> ReadNamedEntries(string archivePath)
		{
			if (File.Exists(archivePath))
				return Read(archivePath).Named;

			var named = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
			var assets = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
			var golden = Path.Combine(Path.GetDirectoryName(assets)!, "Golden");

			foreach (var f in Directory.EnumerateFiles(assets, "*.bin"))
			{
				// corpus-extract.bin is this archive's PREDECESSOR - its corpus half is being rebuilt from the captures
				// right here, so packing it as a named blob would ship the same bodies twice.
				if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(archivePath), StringComparison.Ordinal) ||
				    Path.GetFileName(f) == "corpus-extract.bin")
					continue;

				named[$"assets/{Path.GetFileName(f)}"] = File.ReadAllBytes(f);
			}

			if (Directory.Exists(golden))
			{
				foreach (var f in Directory.EnumerateFiles(golden, "*.bin"))
					named[$"golden/{Path.GetFileName(f)}"] = File.ReadAllBytes(f);
			}

			Console.WriteLine($"  seeded {named.Count} named fixtures from the loose Assets/ + Golden/ files (no archive at {archivePath} yet)");
			return named;
		}

		/// <summary>One corpus body of the archive, for the #555 selection measurement.</summary>
		public readonly record struct CorpusBody(uint Iir, byte[] Body);

		/// <summary>
		///     The corpus half's bodies and their IIR, for <c>--report fixture-stats</c> (Gitea #555).
		/// </summary>
		/// <remarks>
		///     ⚠️ Returns the 0x2000 BODY rather than the full packet - the same span <c>CorpusExtract.Entry.Body</c>
		///     hands the gates - because the framing head carries a length and a CRC that differ on every packet and
		///     would make every body look distinct.
		/// </remarks>
		public static IReadOnlyList<CorpusBody> ReadCorpusForStats(string archivePath)
		{
			return Read(archivePath).Corpus.Select(e =>
			{
				var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(e.Full.AsSpan(8, 4));
				return new CorpusBody(e.Iir, e.Full[(12 + headerLength)..]);
			}).ToList();
		}

		private static (Dictionary<string, int> Counts, List<CorpusEntry> Corpus, SortedDictionary<string, byte[]> Named) Read(string archivePath)
		{
			var raw = File.ReadAllBytes(archivePath);
			if (Encoding.ASCII.GetString(raw, 0, 4) != Magic)
				throw new InvalidDataException($"{archivePath} is not a fixture archive (bad magic).");

			var manifestLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
			using var manifestStream = new MemoryStream(raw, 8, manifestLength);
			using var manifestInflated = new MemoryStream();
			using (var deflate = new DeflateStream(manifestStream, CompressionMode.Decompress))
				deflate.CopyTo(manifestInflated);

			using var doc = JsonDocument.Parse(manifestInflated.ToArray());
			using var payloadStream = new MemoryStream(raw, 8 + manifestLength, raw.Length - 8 - manifestLength);
			using var payload = new MemoryStream();
			using (var deflate = new DeflateStream(payloadStream, CompressionMode.Decompress))
				deflate.CopyTo(payload);

			var bytes = payload.ToArray();
			var offset = 0;
			var counts = new Dictionary<string, int>();
			foreach (var c in doc.RootElement.GetProperty("counts").EnumerateObject())
				counts[c.Name] = c.Value.GetInt32();

			var corpus = new List<CorpusEntry>();
			foreach (var e in doc.RootElement.GetProperty("corpus").EnumerateArray())
			{
				var length = e.GetProperty("n").GetInt32();
				var full = bytes[offset..(offset + length)];
				offset += length;

				var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
				var iir = BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(12 + headerLength + 4, 4));
				corpus.Add(new CorpusEntry(e.GetProperty("s").GetString()!, e.GetProperty("i").GetInt32(), e.GetProperty("f").GetInt32(), e.GetProperty("o").GetUInt32(), iir, full));
			}

			var named = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
			foreach (var e in doc.RootElement.GetProperty("named").EnumerateArray())
			{
				var length = e.GetProperty("n").GetInt32();
				named[e.GetProperty("k").GetString()!] = bytes[offset..(offset + length)];
				offset += length;
			}

			if (offset != bytes.Length)
				throw new InvalidDataException($"{archivePath} is inconsistent: the manifest describes {offset} bytes but the payload holds {bytes.Length}.");

			return (counts, corpus, named);
		}

		private sealed record CorpusEntry(string Source, int Index, int Frame, uint Opcode, uint Iir, byte[] Full);
	}
}
