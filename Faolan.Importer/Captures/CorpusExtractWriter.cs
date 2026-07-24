using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Faolan.Core.Enums;

namespace Faolan.Importer.Captures
{
	// Writes the CORPUS EXTRACT: the handful of packet bodies the test suite actually reads, pulled out of the 349 MB
	// capture corpus so the suite can run from a plain clone.
	//
	// Why this exists: 18 of ~1046 tests need captures, and between them they read only six IIR types - 44 MB of
	// bodies out of 349 MB on disk, which deflates to ~9.5 MB. That is committable; the corpus is not, and it lives
	// in the (private) ProjectFaolan-Data repo.
	//
	// ⚠️ The extract is DERIVED. If a regeneration ever silently drops packets, the whole-corpus gates get weaker
	// while still passing - which is precisely the failure mode those gates exist to catch. So the manifest pins the
	// per-IIR counts and the reader ASSERTS them; a short extract fails loudly instead of quietly narrowing coverage.
	//
	// Format (all little-endian, the on-disk container only - packet bodies keep their own big-endian wire order):
	//   "FCX1"                     magic
	//   u32 manifestLength
	//   manifest                   UTF-8 JSON: { counts: {iir: n}, entries: [{ s: source, i: packetIndex, n: bytes }] }
	//   deflate                    the bodies, concatenated in manifest order
	public static class CorpusExtractWriter
	{
		public const string Magic = "FCX1";

		// The IIR types the test suite reads. Adding a test that needs another type means adding it here and
		// regenerating - the extract deliberately freezes the set rather than guessing.
		public static readonly (uint Iir, string Name)[] WantedIirs =
		[
			((uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR, "snapshot tails, protobuf root, death vitals, quest regions"),
			((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR, "spawn - the protobuf round-trip corpus"), ((uint)GameServer0X2000Opcodes.MoveTaskIIR, "MoveTask"),
			((uint)GameServer0X2000Opcodes.QuestFullUpdateIIR, "QuestFullUpdate"), ((uint)GameServer0X2000Opcodes.QuestJournalIIR, "QuestJournal"),
			((uint)GameServer0X2000Opcodes.QuestLogListIIR, "QuestLogList"), ((uint)GameServer0X2000Opcodes.PlayfieldFIIR, "map change - NpcSpawnExtractor needs it to key spawns to a map"),
			((uint)GameServer0X2000Opcodes.TradeskillReplyIIR, "the one captured recipe catalog")
		];

		// Reads the tshark JSON DIRECTLY rather than going through TsharkCaptureReader, because the tests consume the
		// raw `BodyHex` bytes while CapturedPacket.Bytes is the REBUILT full packet (framing included). Extracting
		// what the tests actually read keeps their logic untouched and the extract byte-identical to the corpus.
		public static void Write(string capturesDir, string outputPath)
		{
			ArgumentNullException.ThrowIfNull(capturesDir);

			var wanted = WantedIirs.Select(w => w.Iir).ToHashSet();
			var entries = new List<object>();
			var counts = new Dictionary<string, int>();
			var sources = 0;
			using var bodies = new MemoryStream();

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

					entries.Add(new
					{
						s = name,
						i,
						n = full.Length,
						f = frame,
						o = opcode
					});
					var key = $"0x{iir:x8}";
					counts[key] = counts.GetValueOrDefault(key) + 1;
					bodies.Write(full, 0, full.Length);
				}
			}

			// Every file in the corpus, not just the ones we extracted bodies from. The doc linters only need NAMES -
			// ProtocolCitationTests checks a [V2:] citation names a real capture, DocReferenceTests resolves
			// Captures/... paths - so carrying the list lets them run without the capture repo too.
			var files = Directory.EnumerateFiles(capturesDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();

			var manifest = JsonSerializer.SerializeToUtf8Bytes(new
			{
				counts,
				files,
				entries
			});

			using var output = File.Create(outputPath);
			output.Write(Encoding.ASCII.GetBytes(Magic));
			Span<byte> len = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)manifest.Length);
			output.Write(len);
			output.Write(manifest);

			bodies.Position = 0;
			using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, true))
				bodies.CopyTo(deflate);

			Console.WriteLine($"Corpus extract -> {outputPath}");
			Console.WriteLine($"  {files.Count} capture file names recorded (for the doc linters)");
			Console.WriteLine($"  {entries.Count} packet bodies from {sources} captures, " + $"{bodies.Length / 1048576.0:F1} MB raw -> {new FileInfo(outputPath).Length / 1048576.0:F1} MB on disk");
			foreach (var (iir, name) in WantedIirs)
				Console.WriteLine($"    {counts.GetValueOrDefault($"0x{iir:x8}"),6}  0x{iir:X8}  {name}");
		}
	}
}
