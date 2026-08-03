using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Constants;
using Faolan.Core.Crypt;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report scaffold-heap`: mine the RETAIL SERVER's own heap out of the NPC spawn scaffold (Gitea #604).
	//
	// The FA015807 pre-IIR scaffold is a 53-word STAT BLOCK (#603, `SpawnPreIir.WordStatIds`). On an NPC spawn the
	// retail server fills only THREE of the 53 words, and sends the other 50 carrying whatever was in that memory -
	// so every captured NPC spawn ships ~200 bytes of retail server-side data that has nothing to do with that NPC.
	// This is the harvest of it: a window into the retail server that nothing else in the corpus gives.
	//
	// ⚠️ It is a DIAGNOSTIC, never a source of content. Nothing here describes the NPC it arrived on, and none of it
	// may be lifted into a table. What it does give is names: the retail script engine's ScriptGroup catalogue, its
	// ScriptCommand class inventory (with source file and line), and the odd `remoteformat` fragment.
	//
	// ⚠️ WHY THIS IS A REPORT AND NOT A SCRIPT. The numbers in the issue that opened this ("133 ScriptGroups", "121
	// source files", "26 key= values across categories 100/50000/50002/50050/50200/51000/52000/55000") came from a
	// scratch probe that ran its regexes over the WHOLE 228-byte buffer, letting a match span the NON-PRINTABLE gaps
	// between one leaked string and the next. That manufactures pairs: `category=` in one string and `000` from the
	// binary after it reads as category 50000. Every count below is taken per PRINTABLE RUN, which is the only
	// boundary a real string can have, and the honest numbers are roughly half the probe's. See ExtractRuns.
	public static partial class ReportCli
	{
		// A leaked string must be a maximal run of printable bytes: a run boundary IS a string boundary, because the
		// bytes on either side of it were never part of one C string. Six is the same floor SpawnPreIir.DecodeScriptText
		// uses - below it, float bytes that happen to be printable start outnumbering text.
		private const int MinRunLength = 6;

		// The four 4-byte slots of the 0x184..0x267 region the client reader never writes (SpawnPreIir has 53 words
		// for 57 slots), as offsets into the reassembled buffer. Nothing is sent for them, so they read as zero and
		// SPLIT any leaked string that spans one. They are a fixed, permanent hole in this view - a string cut by one
		// is only recoverable from another block that happened to catch it at a different alignment.
		private static readonly int[] BufferHoles = [116, 136, 168, 204];

		// `--report-arg dump` also writes every distinct block as a hex dump; `runs` lists every distinct string.
		private static void ScaffoldHeap(IReadOnlyList<NamedCapture> captures, string? mode, string? rdbDir)
		{
			var blocks = new Dictionary<string, int>(StringComparer.Ordinal);
			var spawns = 0;

			foreach (var c in captures)
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn))
						continue;

					// NPCs only. A player's block is that character's real PvP career (#603) - it is not a leak, and
					// mixing it in would attribute the player's own stats to the retail server's heap.
					if (spawn.Handle >= EntityHandles.CharacterInstanceBase || spawn.Character?.player_stats != null)
						continue;

					spawns++;
					var key = Convert.ToHexString(spawn.PreIir.DecodeScriptBuffer());
					blocks[key] = blocks.GetValueOrDefault(key) + 1;
				}

			// run -> how many SPAWNS carried it (not how many distinct blocks), so a string's weight reflects how
			// often retail actually shipped it.
			var runs = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var (hex, n) in blocks)
				foreach (var run in ExtractRuns(Convert.FromHexString(hex)))
					runs[run] = runs.GetValueOrDefault(run) + n;

			var leaked = blocks.Sum(b => (long)b.Value) * (SpawnPreIir.WordCount - 3) * 4;
			Console.WriteLine("scaffold-heap: the retail server's own memory, harvested out of the NPC spawn scaffold (Gitea #604).");
			Console.WriteLine("Retail writes 3 of the 53 scaffold words on an NPC spawn; the other 50 carry whatever was there.");
			Console.WriteLine();
			Console.WriteLine($"{spawns} NPC spawns over {captures.Count} captures -> {blocks.Count} distinct 228-byte blocks, "
				+ $"{leaked / 1024 / 1024.0:F1} MB of unwritten server memory");
			Console.WriteLine($"{runs.Count} distinct printable runs (>= {MinRunLength} chars)");
			Console.WriteLine($"⚠️ {BufferHoles.Length} slots of the region are never written by the client reader either "
				+ $"(buffer offsets {string.Join(", ", BufferHoles)}); a string spanning one is split and only");
			Console.WriteLine("   recoverable from another block that caught it at a different alignment.");

			ScaffoldHeapScriptEngine(runs);
			ScaffoldHeapCallStacks(runs);
			ScaffoldHeapDeployment(runs);
			ScaffoldHeapRemoteFormat(runs, rdbDir);
			ScaffoldHeapSafetySweep(runs);

			switch (mode)
			{
				case "runs":
					Console.WriteLine();
					Console.WriteLine("--- every distinct run ---");
					foreach (var (run, n) in runs.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal))
						Console.WriteLine($"{n,6}  {run}");
					break;

				case "dump":
					var path = Path.Combine(Path.GetTempPath(), "scaffold-heap.txt");
					WriteBlockDump(blocks, path);
					Console.WriteLine();
					Console.WriteLine($"wrote {blocks.Count} block dumps to {path}");
					break;
			}
		}

		// The retail script engine's own names, as the server's error formatter wrote them:
		//   "<file>.cpp(<line>) : Error in \"ScriptFormula defined at <file>.cpp(<line>) in ScriptGroup '<name>'
		//    (id=<id>) Script '<name>' (id=<n>) Command '<name>' (id=<n>) ...\""
		//
		// Every pattern below is anchored on its CLOSING delimiter, so a name truncated by the 228-byte window is
		// dropped rather than half-reported. That is the whole difference between this and the scratch probe.
		private static void ScaffoldHeapScriptEngine(Dictionary<string, int> runs)
		{
			// ⚠️ `id` is a per-parent INDEX for Script and Command (`Script 'main' (id=1)`, and the same command name
			// appears with a dozen different ids), and a large sparse key for ScriptGroup. Only the ScriptGroup one
			// is an identity.
			var groups = Collect(runs, @"ScriptGroup '([^']+)' \(id=(\d+)\)");
			var scripts = Collect(runs, @"Script '([^']+)' \(id=(\d+)\)");
			var commands = Collect(runs, @"Command '([^']+)' \(id=(\d+)\)");

			// ⚠️ A source path is the ONE field here with no closing delimiter to anchor on: `([A-Za-z0-9_/]+\.cpp)`
			// matches from wherever the run happens to begin, so a run that starts mid-path yields `and.cpp`, `d.cpp`,
			// `criptcommands/dbprintfscriptcommand.cpp` - which read as 100 distinct files when there are 32. The
			// anchor is the message's own `defined at ` prefix, which proves the path START was captured.
			var sites = Collect(runs, @"defined at ([A-Za-z0-9_/]+\.cpp)\((\d+)\)");
			var anySite = Collect(runs, @"([A-Za-z0-9_/]+\.cpp)\((\d+)\)");

			Console.WriteLine();
			Console.WriteLine($"--- ScriptGroup name -> id ({groups.Count} complete pairs) ---");
			Console.WriteLine("⚠️ These ids resolve against NOTHING we hold: not an RDB entry id, not any integer column");
			Console.WriteLine("   in faolan.db, and not a hash of the group name. They are the retail server's own keys.");
			foreach (var (g, n) in groups.OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase))
				Console.WriteLine($"{n,6}  {g.Id,10}  {g.Name}");

			var files = sites.GroupBy(s => s.Key.Name).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
			Console.WriteLine();
			Console.WriteLine($"--- ScriptCommand source files ({files.Count} confirmed, {sites.Count} file+line sites) ---");
			Console.WriteLine("The retail server's C++ script-engine layer, named by its own assertion text.");
			foreach (var f in files)
				Console.WriteLine($"{f.Sum(s => s.Value),6}  {f.Key} ({string.Join(", ", f.Select(s => s.Key.Id).OrderBy(l => l))})");

			// A path seen only WITHOUT the anchor is either a truncation of a confirmed one (accounted for) or a file
			// we have genuinely seen and cannot name (a lower bound on what is still out there). Reporting the second
			// group is the honest way to say "there are more" without inventing names for them.
			var confirmed = files.Select(f => f.Key).ToList();
			var unnamed = anySite.Select(s => s.Key.Name)
				.Distinct(StringComparer.Ordinal)
				.Where(n => !confirmed.Any(c => c.EndsWith(n, StringComparison.Ordinal)))
				.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
				.ToList();

			Console.WriteLine();
			Console.WriteLine($"{unnamed.Count} further path(s) were seen only truncated (no `defined at ` prefix in the run) and are");
			Console.WriteLine("not a suffix of any confirmed path - so they are ADDITIONAL files whose full name the corpus has");
			Console.WriteLine("not yet caught. Listed as the fragments they are, never completed by guesswork:");
			foreach (var n in unnamed)
				Console.WriteLine($"        ...{n}");

			Console.WriteLine();
			Console.WriteLine($"--- Command names ({commands.Select(c => c.Key.Name).Distinct().Count()} distinct, "
				+ $"{commands.Count} name+index pairs) ---");
			Console.WriteLine(string.Join(", ", commands.Select(c => c.Key.Name).Distinct().OrderBy(c => c, StringComparer.Ordinal)));

			Console.WriteLine();
			Console.WriteLine($"--- Script names ({scripts.Select(s => s.Key.Name).Distinct().Count()} distinct, "
				+ $"{scripts.Count} name+index pairs) ---");
			Console.WriteLine(string.Join(", ", scripts.Select(s => s.Key.Name).Distinct().OrderBy(s => s, StringComparer.Ordinal)));
		}

		// The `remoteformat` XML the server was holding - the same layer `RemoteFormatXml` builds outbound, so its
		// references are readable with the tooling we already have.
		//
		// ⚠️ TWO ESCAPING DEPTHS, and missing the second one costs the whole yield. A `<remoteformat>` element the
		// server holds as a STANDALONE string is escaped once (`key="a&#92;b"`); one nested inside a `<parameter
		// value="...">` attribute is escaped TWICE (`&lt;localized ... key=&quot;a&amp;#92;b&quot; &gt;`). Matching
		// only the double form finds the plaintext categories 100/51000 and concludes there is nothing to decrypt -
		// which is wrong: the singly-escaped form is where category 50000 turns up.
		private static void ScaffoldHeapRemoteFormat(Dictionary<string, int> runs, string? rdbDir)
		{
			var categories = Collect(runs, @"category=(?:&quot;|"")(\d+)(?:&quot;|"")");
			var tokens = Collect(runs, @"token=(?:&quot;|"")([A-Za-z0-9_]+)(?:&quot;|"")");

			// A COMPLETE reference: id, category and key all present in one run, in the element's own field order.
			// Anything less cannot be used to decrypt, so it is counted but not listed as a find.
			var refs = Collect(runs, @"id=""(\d+)"" category=""(\d+)"" key=""([^""]*)""", 3);
			var nested = Collect(runs, @"id=&quot;(\d+)&quot; category=&quot;(\d+)&quot; flags=&quot;[^&]*&quot; key=&quot;((?:[^&]|&(?!quot;))*)&quot;", 3);
			foreach (var (k, n) in nested)
				refs[k] = refs.GetValueOrDefault(k) + n;

			Console.WriteLine();
			Console.WriteLine("--- remoteformat / LDB fragments ---");
			Console.WriteLine($"categories: {string.Join(", ", categories.OrderBy(c => c.Key.Name).Select(c => $"{c.Key.Name} (x{c.Value})"))}");
			Console.WriteLine($"tokens: {string.Join(", ", tokens.Select(t => $"{t.Key.Name} (x{t.Value})"))}");
			Console.WriteLine($"complete id+category+key references: {refs.Count}");
			// Capture order is (id, category, key), so Name=id, Id=category, Extra=key.
			foreach (var (r, n) in refs.OrderBy(r => r.Key.Id, StringComparer.Ordinal).ThenBy(r => r.Key.Name, StringComparer.Ordinal))
				Console.WriteLine($"{n,6}  LDB[{r.Id}][{r.Name}] key=\"{r.Extra}\"");

			// The point of harvesting keys at all: the eleven ENCRYPTED LDB categories (NPC dialogue 50000-50003,
			// quest text 50300-50306) need one key PER STRING, and a key found here decrypts a string nothing else in
			// the corpus can reach. Everything else in the LDB is plaintext, so a key against it is inert.
			var usable = refs.Keys.Where(r => int.TryParse(r.Id, out var c) && IsEncryptedLdbCategory(c)).ToList();
			Console.WriteLine();
			if (usable.Count == 0)
			{
				Console.WriteLine("No complete reference names an ENCRYPTED category (50000-50003 / 50300-50306), so nothing here");
				Console.WriteLine("decrypts: every category above is plaintext and needs no key.");
				return;
			}

			Console.WriteLine($"⚠️ {usable.Count} reference(s) name an ENCRYPTED category - each one is a dialogue/quest string");
			Console.WriteLine("   recovered from the retail server's memory, readable nowhere else in the corpus:");
			if (rdbDir == null || !Directory.Exists(rdbDir))
			{
				foreach (var r in usable)
					Console.WriteLine($"   LDB[{r.Id}][{r.Name}] - pass --RdbDir to decrypt (or: --report ldb-decrypt --report-arg {r.Id}:{r.Name}:{r.Extra})");

				return;
			}

			var ciphertext = EncryptedLdbBlobs(rdbDir);
			foreach (var r in usable)
			{
				var plain = ciphertext.TryGetValue((uint.Parse(r.Id), uint.Parse(r.Name)), out var blob)
					? LdbKey.Decrypt(blob, LdbKey.Decode(r.Extra))
					: null;

				Console.WriteLine($"   LDB[{r.Id}][{r.Name}] = {plain ?? "(not on an English encrypted page, or the key belongs to a different string)"}");
			}
		}

		// The retail server's own CONFIGURATION TREE and DATABASE SCHEMA - two leaks the first pass over this data
		// missed entirely, and the most interesting thing in it after the script engine. Neither is anything we can
		// import; both are structural facts about how the retail service was deployed.
		private static void ScaffoldHeapDeployment(Dictionary<string, int> runs)
		{
			// e.g. `Universe[1]/Territory[1]/Dimension[2]/GameServer[5]/GMPasswordEnabled`. The bracketed indices are
			// what make it worth reporting: they say a Dimension (our Realm) ran a DOUBLE-DIGIT number of GameServer
			// processes, which is the deployment shape #324's multi-realm work is aiming at.
			var config = Collect(runs, @"(Universe(?:\[\d+\])?(?:/[A-Za-z0-9_]+(?:\[\d+\])?)+)");
			var configPaths = Maximal(config.Keys.Select(c => c.Name));
			Console.WriteLine();
			Console.WriteLine($"--- retail server configuration tree ({configPaths.Count} path(s)) ---");
			Console.WriteLine("Anchored on `Universe`, the root, so a path listed here begins where retail's own does; a path");
			Console.WriteLine("cut by the window is a PREFIX of a longer one, so only the maximal paths are listed.");
			Console.WriteLine("⚠️ The bracketed indices are the find: one Dimension ran at least 12 GameServer processes.");
			foreach (var c in configPaths.OrderBy(c => c, StringComparer.Ordinal))
				Console.WriteLine($"{config.Where(k => k.Key.Name == c).Sum(k => k.Value),6}  {c}");

			// Oracle-style bind parameters (`:1`), so retail's persistence was Oracle. Reported as the fragments they
			// are: a statement cut by the 228-byte window is not a schema, it is evidence of one.
			var sql = Collect(runs, @"([A-Z_]{3,}(?:\s*,\s*[A-Z_]{3,})*\s+from\s+[A-Z_]{3,}[^|]*)");
			var tables = Maximal(Collect(runs, @"\bfrom\s+([A-Z][A-Z_]{2,})\b").Keys.Select(t => t.Name));
			Console.WriteLine();
			Console.WriteLine($"--- retail server SQL ({tables.Count} table name(s), Oracle bind syntax `:1`) ---");
			Console.WriteLine(string.Join(", ", tables.OrderBy(t => t, StringComparer.Ordinal)));

			// A statement fragment is cut at BOTH ends by the window, so containment - not the prefix rule the table
			// names use - is what folds the partial sightings into the longest one.
			var statements = sql.Keys.Select(s => s.Name.Trim()).Distinct(StringComparer.Ordinal).ToList();
			foreach (var st in statements.Where(a => !statements.Any(b => b.Length > a.Length && b.Contains(a, StringComparison.Ordinal))).OrderBy(a => a, StringComparer.Ordinal))
				Console.WriteLine($"       ...{st}");
		}

		// The script engine's CALL STACK, the other form its diagnostics take:
		//   `(0:bccswitchstate(33)|main(1)|DefaultIdleStates`  =  depth : command(index) | script(index) | group
		// A second, independent naming of the same three levels the error text names - and the group name here has no
		// closing delimiter, so it is reported as a stack shape rather than mined for names.
		private static void ScaffoldHeapCallStacks(Dictionary<string, int> runs)
		{
			var frames = Collect(runs, @"\((\d+):([a-z]+)\((\d+)\)\|", 3);
			Console.WriteLine();
			Console.WriteLine($"--- script call-stack frames ({frames.Count} distinct) ---");
			Console.WriteLine("Format: (depth:command(index)|script(index)|ScriptGroup - the same three levels the error text names.");
			foreach (var (f, n) in frames.OrderByDescending(f => f.Value))
				Console.WriteLine($"{n,6}  depth {f.Name}: {f.Id}({f.Extra})");
		}

		// The eleven categories whose TDC2 page header carries flags=1 (see LdbStringIndex). Everything else in the
		// LDB is plaintext, so a key attribute against it is inert.
		private static bool IsEncryptedLdbCategory(int category)
		{
			return category is >= 50000 and <= 50003 or >= 50300 and <= 50306;
		}

		// What ELSE is in retail's memory. This is the check that decides whether the harvest can be quoted in a
		// public doc at all, so it is shipped rather than asserted: re-run it and it re-derives its own verdict.
		private static void ScaffoldHeapSafetySweep(Dictionary<string, int> runs)
		{
			(string Kind, string Pattern)[] probes =
			[
				("IPv4 address", @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"),
				("URL", @"\b(?:https?|ftp)://\S+"),
				("e-mail", @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"),
				("GUID", @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"),
				("Windows path", @"\b[A-Za-z]:\\\\?[^\s]*"),
				("credential-ish", @"(?i)\b(?:password|passwd|secret|apikey|api_key|private_key|bearer)\b"),
				("SQL fragment", @"(?i)\b(?:select|insert|update|delete)\s+.*?\b(?:from|into|set|where)\b|\bwhere\s+[A-Za-z_][A-Za-z0-9_]*\s*[=<>]")
			];

			Console.WriteLine();
			Console.WriteLine("--- safety sweep (is this quotable?) ---");
			foreach (var (kind, pattern) in probes)
			{
				var hits = Collect(runs, "(" + pattern + ")");
				Console.WriteLine(hits.Count == 0
					? $"  clean   {kind}"
					: $"  {hits.Count,5}   {kind}: {string.Join(" | ", hits.OrderByDescending(h => h.Value).Take(5).Select(h => h.Key.Name.Trim()))}");
			}
		}

		// The entries of `values` that are not a proper PREFIX of another. A leaked string is cut at the END by the
		// 228-byte window, so `QUEST_RE` beside `QUEST_REWARD` is one table seen twice, not two - the same truncation
		// trap the source-path list handles with its `defined at ` anchor, in the one form that needs no anchor.
		private static List<string> Maximal(IEnumerable<string> values)
		{
			var all = values.Distinct(StringComparer.Ordinal).ToList();
			return all.Where(v => !all.Any(o => o.Length > v.Length && o.StartsWith(v, StringComparison.Ordinal))).ToList();
		}

		// Every match of `pattern` across every run, keyed by its first `groups` capture groups and weighted by how
		// many SPAWNS carried the run it came from (not how many distinct blocks) - so a string's weight is how often
		// retail actually shipped it.
		private static Dictionary<(string Name, string Id, string Extra), int> Collect(Dictionary<string, int> runs, string pattern, int groups = 2)
		{
			var found = new Dictionary<(string, string, string), int>();
			var re = new Regex(pattern, RegexOptions.CultureInvariant);
			foreach (var (run, n) in runs)
				foreach (Match m in re.Matches(run))
				{
					var key = (Group(m, 1), groups >= 2
						? Group(m, 2)
						: "", groups >= 3
							? Group(m, 3)
							: "");

					found[key] = found.GetValueOrDefault(key) + n;
				}

			return found;

			static string Group(Match m, int i)
			{
				return m.Groups.Count > i
					? m.Groups[i].Value
					: "";
			}
		}

		// The maximal printable runs of a reassembled block. See the class comment for why the boundary matters.
		private static IEnumerable<string> ExtractRuns(byte[] buffer)
		{
			var run = new StringBuilder();
			foreach (var c in buffer)
			{
				if (c is >= 0x20 and < 0x7F)
					run.Append((char)c);
				else
				{
					if (run.Length >= MinRunLength)
						yield return run.ToString();

					run.Clear();
				}
			}

			if (run.Length >= MinRunLength)
				yield return run.ToString();
		}

		private static void WriteBlockDump(Dictionary<string, int> blocks, string path)
		{
			using var w = new StreamWriter(path);
			foreach (var (hex, n) in blocks.OrderByDescending(b => b.Value))
			{
				var bytes = Convert.FromHexString(hex);
				w.WriteLine($"### x{n}");
				for (var off = 0; off < bytes.Length; off += 16)
				{
					var len = Math.Min(16, bytes.Length - off);
					var line = new StringBuilder().Append((0x184 + off).ToString("x3")).Append("  ");
					for (var i = 0; i < 16; i++)
					{
						line.Append(i < len
							? bytes[off + i].ToString("x2")
							: "  ").Append(' ');
					}

					line.Append(' ');
					for (var i = 0; i < len; i++)
					{
						line.Append(bytes[off + i] is >= 0x20 and < 0x7F
							? (char)bytes[off + i]
							: '.');
					}

					w.WriteLine(line.ToString());
				}
			}
		}
	}
}
