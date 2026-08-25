using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Faolan.Core.Extensions;

namespace Faolan.Tests.Support
{
	// THE fixture file: Faolan.Tests/Assets/fixtures.bin holds every byte of capture-derived data the suite reads -
	// the whole-corpus slice AND the hand-carved named fixtures (the former loose Assets/*.bin inputs and
	// Golden/*.bin expected outputs). One file, so the repo carries one blob rather than 43.
	//
	// Written by `dotnet run --project Faolan.Importer -- --report fixtures` (FixtureArchiveWriter owns the format;
	// `--report fixture-set` adds or replaces one named entry).
	//
	// Three readers sit on top and are what tests actually use:
	//   CorpusExtract - the corpus half, by IIR type
	//   Golden        - expected outputs, by name
	//   TestPaths     - named inputs, as bytes or as a materialized path
	internal static class FixtureArchive
	{
		public const string Magic = "FCX3";

		// The DEFLATE-era magic, still read so a checkout with the older archive opens rather than failing on the
		// magic. `--report fixture-repack` converts one; nothing writes it.
		public const string LegacyDeflateMagic = "FCX2";

		private static readonly Lazy<Archive> Loaded = new Lazy<Archive>(Load);
		private static readonly Lazy<Dictionary<string, string>> Materialized = new Lazy<Dictionary<string, string>>(Materialize);

		public static string Path => TestPaths.Repo("Faolan.Tests", "Assets", "fixtures.bin");

		// The corpus entries, in CAPTURE order - the order the gates that walk a session (e.g. "the spell book is
		// absent only in the session's FIRST snapshot") depend on. The container stores them grouped by shape instead,
		// purely so they compress; restoring the order here is what keeps that a container concern.
		//
		// "Capture order" is WITHIN a capture (by packet index); BETWEEN captures the order is by capture id, which is
		// deterministic but arbitrary - no gate may rely on which capture comes first (#465).
		public static IReadOnlyList<CorpusRecord> Corpus => Loaded.Value.Corpus;

		public static byte[] Named(string key)
		{
			if (!Loaded.Value.Named.TryGetValue(key, out var body))
			{
				throw new FileNotFoundException(
					$"No fixture '{key}' in {Path}. The named fixtures are hand-carved and cannot be re-derived from the " +
					$"captures - add one with: dotnet run --project Faolan.Importer -- --report fixture-set --report-arg {key}=<file>. " +
					$"Present: {string.Join(", ", Loaded.Value.Named.Keys.Take(8))}...");
			}

			return body;
		}

		// The same bytes as a FILE, for the tests that hand a path to something that opens it. Materialized under the
		// test project's obj/ (gitignored, and NOT the system temp dir - a ramdisk /tmp is what the seed-fixture cache
		// once filled), mirroring the archive's own key layout: obj/fixtures/assets/<name>, obj/fixtures/golden/<name>.
		//
		// The cache is filled in FULL on first use rather than per key, because tests reach fixtures by directory as
		// well as by name (`Path.Combine(GetDirectoryName(oneAsset), "another.bin")`) - a lazily-populated directory
		// makes that work or not depending on which test ran first. ~40 entries, ~1.4 MB, once per run.
		public static string NamedPath(string key)
		{
			Materialized.Value.TryGetValue(key, out var path);
			return path ?? throw new FileNotFoundException($"No fixture '{key}' in {Path}.", Path);
		}

		private static Dictionary<string, string> Materialize()
		{
			var root = TestPaths.Repo("Faolan.Tests", "obj", "fixtures");
			var paths = new Dictionary<string, string>(StringComparer.Ordinal);

			foreach (var (key, body) in Loaded.Value.Named)
			{
				var target = System.IO.Path.Combine(root, key);
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);

				// Write-then-move: xUnit runs collections in parallel and a half-written file read by the loser is a
				// byte-exact failure with no visible cause.
				if (!File.Exists(target) || new FileInfo(target).Length != body.Length)
				{
					var partial = $"{target}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.partial";
					File.WriteAllBytes(partial, body);
					File.Move(partial, target, true);
				}

				paths[key] = target;
			}

			return paths;
		}

		private static Archive Load()
		{
			if (!File.Exists(Path))
			{
				throw new FileNotFoundException(
					$"Fixture archive not found at {Path}. Regenerate it with the capture repo present: " +
					"dotnet run --project Faolan.Importer -- --report fixtures --captures <data-repo>/Captures", Path);
			}

			var raw = File.ReadAllBytes(Path);
			var magic = Encoding.ASCII.GetString(raw, 0, 4);
			if (magic != Magic && magic != LegacyDeflateMagic)
				throw new InvalidDataException($"{Path} is not a fixture archive (bad magic).");

			var brotli = magic == Magic;
			var manifestLength = (int)raw.ReadU32Le(4);
			var manifest = Inflate(raw, 8, manifestLength, brotli);
			var payload = Inflate(raw, 8 + manifestLength, raw.Length - 8 - manifestLength, brotli);

			using var doc = JsonDocument.Parse(manifest);
			var offset = 0;
			var corpus = new List<CorpusRecord>();
			foreach (var e in doc.RootElement.GetProperty("corpus").EnumerateArray())
			{
				var length = e.GetProperty("n").GetInt32();
				var full = payload[offset..(offset + length)];
				offset += length;

				var headerLength = (int)full.ReadU32Be(8);
				corpus.Add(new CorpusRecord(e.GetProperty("s").GetString()!, e.GetProperty("i").GetInt32(), e.GetProperty("f").GetInt32(),
					e.GetProperty("o").GetUInt32(), full.ReadU32Be(12 + headerLength + 4), full));
			}

			var named = new Dictionary<string, byte[]>(StringComparer.Ordinal);
			foreach (var e in doc.RootElement.GetProperty("named").EnumerateArray())
			{
				var length = e.GetProperty("n").GetInt32();
				named[e.GetProperty("k").GetString()!] = payload[offset..(offset + length)];
				offset += length;
			}

			if (offset != payload.Length)
				throw new InvalidDataException($"{Path} is inconsistent: the manifest describes {offset} bytes but the payload holds {payload.Length}.");

			return new Archive(corpus.OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Index).ToList(), named);
		}

		// ⭐ BROTLI since #555 - the container change that took the file from 15.5 MB to 3.0 MB with no content
		// change at all. Deflate's 32 KB window could not see across 45k near-duplicate bodies; a 16 MB one can.
		// Decoding needs no window hint, so this is one call either way.
		private static byte[] Inflate(byte[] raw, int offset, int length, bool brotli)
		{
			using var source = new MemoryStream(raw, offset, length);
			using var inflated = new MemoryStream();
			using (Stream decoder = brotli ? new BrotliStream(source, CompressionMode.Decompress) : new DeflateStream(source, CompressionMode.Decompress))
				decoder.CopyTo(inflated);

			return inflated.ToArray();
		}

		// One extracted packet: the FULL rebuilt wire packet, plus the provenance and frame the gates report.
		//
		// `Source` is the capture's OPAQUE ID - 8 lower-case hex digits, the head of the SHA-256 of the dump's file
		// name - never the name itself (#465: the dumps are named after the characters played, and a manifest of 50k
		// entries shipped those names ~39,000 times). It is stable across re-harvests and enough to tell captures
		// apart in a failure message; mapping one back to its dump needs the private capture repo
		// (`dotnet run --project Faolan.Importer -- --report fixture-source --report-arg <id> --captures <dir>`).
		public sealed record CorpusRecord(string Source, int Index, int Frame, uint Opcode, uint Iir, byte[] Full);

		private sealed record Archive(IReadOnlyList<CorpusRecord> Corpus, Dictionary<string, byte[]> Named);
	}
}
