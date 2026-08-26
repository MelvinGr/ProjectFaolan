using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
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
	//                                  corpus: [{ s: captureId, i: packetIndex, n: bytes, f: frame, o: opcode }],
	//                                  named:  [{ k: key, n: bytes }] }
	//   deflate                    the payload: corpus entries then named entries, in manifest order
	//
	// Two things about that shape are deliberate, and together they cut the file by a third at identical coverage:
	//   * The manifest is DEFLATED. It is one JSON object per packet and dwarfed the bodies otherwise (4.9 MB plain,
	//     0.27 MB deflated at 45k entries).
	//   * Corpus entries are laid out by (IIR, length), not capture order, so same-shaped bodies sit next to each other
	//     and the compressor sees the redundancy (17.4 MB -> 14.9 MB). Capture order is NOT lost - each entry carries
	//     its capture id and packet index, and the READER sorts back to it, because gates like "the session's first
	//     snapshot" depend on that order.
	//
	// ⭐ `s` is an OPAQUE CAPTURE ID, not the capture's file name (#465). The dumps are named after the characters that
	// were played, so a manifest of 50k entries carried the maintainer's personal character names ~39,000 times; the
	// id is CaptureId(name) - the first 8 hex digits of SHA-256 over the dump's file name - which keeps the provenance
	// (one id per capture, stable across re-harvests, the same "<id>#<index>" a failure prints) without shipping the
	// name. Resolving an id back to its dump needs the private capture repo: `--report fixture-source`.
	public static class FixtureArchiveWriter
	{
		public const string Magic = "FCX3";

		// The DEFLATE-era magic. Still READ so an archive from before the container change opens (and so
		// `--report fixture-repack` can convert one), never written.
		public const string LegacyDeflateMagic = "FCX2";

		// The width of a capture id: 8 hex digits = 32 bits of SHA-256, which is 116 captures with no collision and
		// room for many more before one is plausible (a collision would show up as one id claiming two dumps'
		// packet indexes, which `--report fixture-source` prints).
		private const int CaptureIdLength = 8;

		/// <summary>
		///     The opaque id a capture is known by inside the archive: the first 8 lower-case hex digits of the SHA-256
		///     of its dump's file name (<c>&lt;name&gt;.tshark.json</c>, no directory), the string the harvest sees.
		/// </summary>
		/// <remarks>
		///     One function, used by the harvest, by the repack of an archive that still stores names, and by the
		///     resolver that maps an id back to a dump - so all three agree by construction (#465).
		/// </remarks>
		public static string CaptureId(string captureFileName)
		{
			ArgumentNullException.ThrowIfNull(captureFileName);
			return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(captureFileName)))[..CaptureIdLength];
		}

		/// <summary>Whether a manifest <c>s</c> value is already a capture id rather than a dump's file name.</summary>
		/// <remarks>
		///     Not a heuristic: a harvested name is always <c>&lt;name&gt;.tshark.json</c>, which can never be 8 hex
		///     digits, so the two forms are disjoint and an archive from before #465 converts on read.
		/// </remarks>
		public static bool IsCaptureId(string source)
		{
			ArgumentNullException.ThrowIfNull(source);
			return source.Length == CaptureIdLength && source.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
		}

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

				// The archive knows the capture by its opaque id, never by name (#465) - see CaptureId.
				var name = CaptureId(Path.GetFileName(json));
				var index = 0;
				foreach (var p in packets.EnumerateArray())
				{
					// The packet INDEX must count every packet, not just kept ones: tests report provenance as
					// "<capture id>#<index>" and that index has to keep meaning the same thing.
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

		// Rewrites the archive in the CURRENT container format, byte-for-byte identical in content. The corpus half
		// is derived from the captures and the named half cannot be re-derived at all, so a container change must not
		// require a re-harvest: this converts what is committed instead (#555, FCX2 deflate -> FCX3 brotli; #465,
		// capture NAMES -> capture ids, which Read applies on the way in).
		//
		// The content digests are printed before and after and compared, so "content unchanged" is a checked claim
		// rather than a comment: they cover every corpus body with its index/frame/opcode and every named fixture,
		// and deliberately NOT the capture id or the layout order, both of which a repack is allowed to change.
		public static void Repack(string archivePath)
		{
			var before = new FileInfo(archivePath).Length;
			var (counts, corpus, named) = Read(archivePath);
			var (corpusDigest, namedDigest) = ContentDigest(corpus, named);
			WriteArchive(archivePath, corpus, counts, named);
			var after = new FileInfo(archivePath).Length;

			var reread = Read(archivePath);
			var (corpusAfter, namedAfter) = ContentDigest(reread.Corpus, reread.Named);
			Console.WriteLine($"repacked {archivePath}: {before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB "
			                  + $"({(before > 0 ? (double)before / after : 0):F1}x), {corpus.Count} corpus entries + {named.Count} named fixtures unchanged");
			Console.WriteLine($"  corpus content digest: {corpusDigest} -> {corpusAfter}");
			Console.WriteLine($"  named  content digest: {namedDigest} -> {namedAfter}");
			if (corpusDigest != corpusAfter || namedDigest != namedAfter)
				throw new InvalidDataException($"repack of {archivePath} changed its content - the digests above differ.");
		}

		/// <summary>
		///     A digest of the archive's CONTENT, independent of the layout order and of the capture ids: the corpus
		///     bodies (each with its packet index, frame and opcode) in a canonical order, and the named fixtures by
		///     key. Two archives that hold the same packets and fixtures digest the same however they were written.
		/// </summary>
		/// <remarks>
		///     Recipe, so an outside tool can reproduce it: corpus entries sorted by (full packet bytes, index, frame,
		///     opcode), each hashed as <c>i32 index, i32 frame, u32 opcode</c> (little-endian) then the full packet;
		///     named entries in key order, each as the UTF-8 key then the bytes. SHA-256, lower-case hex.
		/// </remarks>
		private static (string Corpus, string Named) ContentDigest(List<CorpusEntry> corpus, SortedDictionary<string, byte[]> named)
		{
			var canonical = corpus.ToList();
			canonical.Sort((a, b) =>
			{
				var byBytes = a.Full.AsSpan().SequenceCompareTo(b.Full);
				if (byBytes != 0)
					return byBytes;

				var byIndex = a.Index.CompareTo(b.Index);
				if (byIndex != 0)
					return byIndex;

				var byFrame = a.Frame.CompareTo(b.Frame);
				return byFrame != 0 ? byFrame : a.Opcode.CompareTo(b.Opcode);
			});

			using var corpusHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			Span<byte> head = stackalloc byte[12];
			foreach (var e in canonical)
			{
				BinaryPrimitives.WriteInt32LittleEndian(head, e.Index);
				BinaryPrimitives.WriteInt32LittleEndian(head[4..], e.Frame);
				BinaryPrimitives.WriteUInt32LittleEndian(head[8..], e.Opcode);
				corpusHash.AppendData(head);
				corpusHash.AppendData(e.Full);
			}

			using var namedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			foreach (var (key, body) in named)
			{
				namedHash.AppendData(Encoding.UTF8.GetBytes(key));
				namedHash.AppendData(body);
			}

			return (Convert.ToHexStringLower(corpusHash.GetHashAndReset()), Convert.ToHexStringLower(namedHash.GetHashAndReset()));
		}

		/// <summary>
		///     Maps capture ids back to the dumps they were hashed from, by hashing every dump's file name in the
		///     capture repo - the only way to read a "<c>&lt;id&gt;#&lt;index&gt;</c>" provenance once the archive no
		///     longer stores names (#465). Prints one line per requested id; an id no dump hashes to is reported as
		///     such (a dump renamed or removed since the harvest), and every archived id is listed when none is asked
		///     for.
		/// </summary>
		public static int ResolveCaptureIds(string capturesDir, string archivePath, IReadOnlyCollection<string> ids)
		{
			ArgumentNullException.ThrowIfNull(capturesDir);

			var byId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			foreach (var json in Directory.EnumerateFiles(capturesDir, "*.tshark.json").OrderBy(f => f, StringComparer.Ordinal))
			{
				var name = Path.GetFileName(json);
				if (!byId.TryGetValue(CaptureId(name), out var names))
					byId[CaptureId(name)] = names = [];

				names.Add(name);
			}

			var wanted = ids.Count > 0
				? ids
				: Read(archivePath).Corpus.Select(e => e.Source).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

			var unresolved = 0;
			foreach (var id in wanted)
			{
				if (byId.TryGetValue(id, out var names))
				{
					// Two names hashing to one id is the collision the 8-digit width makes improbable - shown, not hidden.
					Console.WriteLine($"{id}  {string.Join("  |  ", names)}{(names.Count > 1 ? "   ⚠️ COLLISION" : "")}");
				}
				else
				{
					unresolved++;
					Console.WriteLine($"{id}  (no dump in {capturesDir} hashes to this id)");
				}
			}

			return unresolved == 0 ? 0 : 1;
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

		// Re-KEYS one named fixture, byte-for-byte. The bytes are the evidence and never change; only the name they
		// are filed under does - which is the whole operation #465 needs, because a fixture KEY is as much a place a
		// character name rides along as a doc or a comment is (`golden/SpawnHoxxlingBuffed.bin`).
		//
		// It exists because that rename had to be done by hand the first time: extract, SetNamed, RemoveNamed, and
		// hope nothing was dropped in between. Doing it in one operation means the archive is never in a state where
		// the fixture is under both keys or neither.
		public static void RenameNamed(string archivePath, string fromKey, string toKey)
		{
			ArgumentNullException.ThrowIfNull(fromKey);
			ArgumentNullException.ThrowIfNull(toKey);

			var (counts, corpus, named) = Read(archivePath);
			if (!named.TryGetValue(fromKey, out var bytes))
			{
				Console.WriteLine($"{fromKey}: not in the archive; nothing renamed ({named.Count} named fixtures)");
				return;
			}

			if (named.ContainsKey(toKey))
			{
				Console.Error.WriteLine($"{toKey} already exists; refusing to overwrite it. Remove it first if that is really the intent.");
				return;
			}

			named.Remove(fromKey);
			named[toKey] = bytes;
			WriteArchive(archivePath, corpus, counts, named);
			Console.WriteLine($"{fromKey} -> {toKey} ({bytes.Length} bytes, unchanged); archive now {named.Count} named fixtures, {corpus.Count} corpus entries");
		}

		// Substitutes personal character names inside the packet BYTES themselves - the last half of #465 the text
		// sweep and the manifest re-key could not reach. Each name rides the wire as a PROTOBUF length-delimited
		// field (`[tag wiretype 2][len varint][latin1]`, `--report fixture-names`), so an EQUAL-LENGTH pseudonym
		// overwrites exactly the name's own bytes: the single-byte length does not move and nothing after it
		// shifts, which is why every byte-exact golden that reads the archive stays byte-exact.
		//
		// ⛔ Only FRAMED occurrences are touched - a hit whose preceding byte is the field's length AND whose byte
		// before that is a wiretype-2 tag. That is what makes it safe against the prefix trap: a 6-char name that is
		// the start of a 7-char one carries the 7's length byte at that position, so its framed check fails there and
		// the shorter name is not substituted inside the longer. Unframed byte coincidences are left untouched.
		//
		// EQUAL LENGTH IS ENFORCED, not assumed: a pair whose two names differ in byte length is refused before
		// anything is written, because a differing length WOULD shift every following field and break the goldens.
		//
		// ⚠️ The golden TEST LITERALS that assert a name physically present in these bytes (`GuildPacketTests`,
		// `MailMessageCodecTests`, `TradepostMailBodyTests`) must move to the same pseudonym in the same change -
		// they compare a freshly BUILT packet, whose name comes from the test's own input, against the substituted
		// golden. This tool does the archive half; those three literals are the coordinated other half.
		public static int Substitute(string archivePath, IReadOnlyList<(string Old, string New)> pairs, bool dryRun)
		{
			ArgumentNullException.ThrowIfNull(pairs);

			foreach (var (oldName, newName) in pairs)
			{
				var oldLen = Encoding.Latin1.GetByteCount(oldName);
				var newLen = Encoding.Latin1.GetByteCount(newName);
				if (oldLen != newLen)
				{
					Console.Error.WriteLine($"fixture-substitute: '{oldName}' ({oldLen} B) -> '{newName}' ({newLen} B) is NOT equal length; refusing. "
					                        + "A differing length shifts every following field and breaks the byte-exact goldens.");
					return 1;
				}
			}

			// Longest old name first, so a name that is the prefix of another is only ever considered after the
			// longer one - belt and braces on top of the framed-length guard, which already separates them.
			var ordered = pairs.OrderByDescending(p => Encoding.Latin1.GetByteCount(p.Old)).ToList();

			var (counts, corpus, named) = Read(archivePath);
			var (corpusBefore, namedBefore) = ContentDigest(corpus, named);

			var perPair = new Dictionary<string, (int Corpus, int Named)>(StringComparer.Ordinal);
			foreach (var (oldName, newName) in ordered)
			{
				var oldPat = Encoding.Latin1.GetBytes(oldName);
				var newPat = Encoding.Latin1.GetBytes(newName);

				var inCorpus = corpus.Sum(e => ReplaceFramed(e.Full, oldPat, newPat));
				var inNamed = named.Values.Sum(b => ReplaceFramed(b, oldPat, newPat));
				perPair[oldName] = (inCorpus, inNamed);
			}

			Console.WriteLine($"fixture-substitute: {archivePath} ({corpus.Count} corpus + {named.Count} named), {(dryRun ? "DRY RUN" : "writing")}");
			var total = 0;
			foreach (var (oldName, newName) in ordered)
			{
				var (c, n) = perPair[oldName];
				total += c + n;
				Console.WriteLine($"  '{oldName}' -> '{newName}': {c} corpus + {n} named framed occurrence(s)");
			}

			if (total == 0)
			{
				Console.WriteLine("  nothing framed to substitute; archive untouched.");
				return 0;
			}

			if (dryRun)
			{
				Console.WriteLine($"  DRY RUN: {total} occurrence(s) would change; nothing written. Re-run without --dry-run to apply.");
				return 0;
			}

			WriteArchive(archivePath, corpus, counts, named);

			// The old names must be GONE from the framed positions, and every body must keep its length (equal-length
			// substitution). Both are re-checked from disk, so "done" is a verified claim.
			var reread = Read(archivePath);
			var residue = ordered.Sum(p =>
			{
				var pat = Encoding.Latin1.GetBytes(p.Old);
				return reread.Corpus.Sum(e => CountFramed(e.Full, pat)) + reread.Named.Values.Sum(b => CountFramed(b, pat));
			});
			var (corpusAfter, namedAfter) = ContentDigest(reread.Corpus, reread.Named);
			Console.WriteLine($"  {total} occurrence(s) substituted; {reread.Corpus.Count} corpus + {reread.Named.Count} named preserved");
			Console.WriteLine($"  corpus content digest: {corpusBefore} -> {corpusAfter}");
			Console.WriteLine($"  named  content digest: {namedBefore} -> {namedAfter}");
			if (residue != 0)
				throw new InvalidDataException($"fixture-substitute left {residue} framed occurrence(s) of an old name behind - the write did not take.");

			Console.WriteLine("  verified: zero framed occurrences of any old name remain.");
			return 0;
		}

		// Overwrites, in place, every PROTOBUF-FRAMED occurrence of oldPat with newPat (which MUST be the same
		// length), and returns how many it changed. Framed = preceded by a length byte equal to the field length and
		// a wiretype-2 tag before that. Case-insensitive match on the name bytes (the wire carries the name as typed,
		// the block list is lower-case; see the FixtureNames scanner).
		internal static int ReplaceFramed(byte[] body, byte[] oldPat, byte[] newPat)
		{
			var changed = 0;
			for (var i = 2; i + oldPat.Length <= body.Length; i++)
			{
				if (body[i - 1] != oldPat.Length || (body[i - 2] & 0x07) != 2)
					continue;

				var match = true;
				for (var j = 0; j < oldPat.Length && match; j++)
					match = FoldByte(body[i + j]) == FoldByte(oldPat[j]);

				if (!match)
					continue;

				newPat.CopyTo(body, i);
				changed++;
				i += oldPat.Length - 1;
			}

			return changed;
		}

		// Counts framed occurrences without changing anything - the post-write verification.
		internal static int CountFramed(byte[] body, byte[] pat)
		{
			var count = 0;
			for (var i = 2; i + pat.Length <= body.Length; i++)
			{
				if (body[i - 1] != pat.Length || (body[i - 2] & 0x07) != 2)
					continue;

				var match = true;
				for (var j = 0; j < pat.Length && match; j++)
					match = FoldByte(body[i + j]) == FoldByte(pat[j]);

				if (match)
					count++;
			}

			return count;
		}

		private static byte FoldByte(byte b)
		{
			return b >= (byte)'A' && b <= (byte)'Z' ? (byte)(b + 32) : b;
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

			var compressedManifest = Compress(manifest);

			Span<byte> len = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)compressedManifest.Length);
			output.Write(len);
			output.Write(compressedManifest);

			using var payload = new MemoryStream();
			foreach (var e in ordered)
				payload.Write(e.Full);
			foreach (var body in named.Values)
				payload.Write(body);

			output.Write(Compress(payload.GetBuffer().AsSpan(0, (int)payload.Length)));
		}

		/// <summary>
		///     Compresses one container section. BROTLI, quality 11, window <b>24</b> - and the window is the whole
		///     point (Gitea #555).
		/// </summary>
		/// <remarks>
		///     ⭐ The archive is 45k packet bodies laid out by (IIR, length), i.e. near-duplicates spread over 81 MB.
		///     DEFLATE can only look 32 KB back, so it compressed each body against its immediate neighbours and got
		///     15.1 MB; a 16 MB window sees the whole class at once and gets <b>2.6 MB</b> for the same bytes. The
		///     manifest halves too (404 KB -&gt; 221 KB). Nothing is dropped and no gate changes - this is the
		///     container, not the content.
		///     <para>
		///         ⚠️ It also DECOMPRESSES faster (0.04 s against deflate's 0.14 s), so the suite pays nothing for it.
		///     </para>
		///     <para>
		///         ⛔ <c>BrotliStream</c> is deliberately not used to WRITE: it fixes the window at 22 bits, which
		///         costs 158 KB here. <c>BrotliEncoder.TryCompress</c> takes the window explicitly. Reading is a
		///         stream either way - a decoder handles any window the encoder chose.
		///     </para>
		/// </remarks>
		private static byte[] Compress(ReadOnlySpan<byte> plain)
		{
			// TryCompress needs the destination up front, and brotli can expand incompressible input slightly.
			var destination = new byte[plain.Length + 1024];
			if (!BrotliEncoder.TryCompress(plain, destination, out var written, BrotliQuality, BrotliWindow))
				throw new InvalidDataException($"brotli refused to compress {plain.Length} bytes into {destination.Length}.");

			return destination[..written];
		}

		private static byte[] Decompress(ReadOnlySpan<byte> compressed, bool brotli)
		{
			using var source = new MemoryStream(compressed.ToArray());
			using var plain = new MemoryStream();
			using (Stream decoder = brotli ? new BrotliStream(source, CompressionMode.Decompress) : new DeflateStream(source, CompressionMode.Decompress))
				decoder.CopyTo(plain);

			return plain.ToArray();
		}

		// Brotli's maximum quality, and a 16 MB window - see Compress.
		private const int BrotliQuality = 11;
		private const int BrotliWindow = 24;

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
		/// <summary>
		///     Both halves of the archive as raw PACKET bytes, for <c>--report fixture-names</c> (Gitea #465).
		/// </summary>
		/// <remarks>
		///     Unlike <see cref="ReadCorpusForStats" /> this keeps the FULL packet, framing head included: a name
		///     scan is looking for bytes anywhere in what is committed, and excluding the head would hide a hit that
		///     is really there.
		/// </remarks>
		public static (IReadOnlyList<ScannedPacket> Corpus, IReadOnlyDictionary<string, byte[]> Named) ReadBodiesForNameScan(string archivePath)
		{
			var (_, corpus, named) = Read(archivePath);
			return (corpus.Select(e => new ScannedPacket(e.Iir, e.Full)).ToList(), named);
		}

		/// <summary>One archive packet reduced to what a name scan needs: its IIR and its whole bytes.</summary>
		public readonly record struct ScannedPacket(uint Iir, byte[] Full);

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
			var magic = Encoding.ASCII.GetString(raw, 0, 4);
			if (magic != Magic && magic != LegacyDeflateMagic)
				throw new InvalidDataException($"{archivePath} is not a fixture archive (bad magic).");

			var brotli = magic == Magic;
			var manifestLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
			using var doc = JsonDocument.Parse(Decompress(raw.AsSpan(8, manifestLength), brotli));
			var bytes = Decompress(raw.AsSpan(8 + manifestLength), brotli);
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

				// An archive from before #465 stores the dump's file NAME here; hash it to the id on the way in, so a
				// repack writes ids and nothing downstream ever sees a name. An id passes through unchanged.
				var source = e.GetProperty("s").GetString()!;
				if (!IsCaptureId(source))
					source = CaptureId(source);

				corpus.Add(new CorpusEntry(source, e.GetProperty("i").GetInt32(), e.GetProperty("f").GetInt32(), e.GetProperty("o").GetUInt32(), iir, full));
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

		// `Source` is the capture id (CaptureId), never a dump's name - Read converts, Write hashes.
		private sealed record CorpusEntry(string Source, int Index, int Frame, uint Opcode, uint Iir, byte[] Full);
	}
}
