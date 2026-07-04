using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.GameServer.Spawn;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// Read-only diagnostics (the old --dump-*/--burst-coverage flags), routed through `--report <name>`.
	// These inspect captures or the DB and print; they never write, so they bypass the import runner entirely.
	public static class ReportCli
	{
		private const string RepoRoot = "/home/melvin/Development/C#/Project Faolan";

		public static async Task<int> RunAsync(string[] args, string report)
		{
			var dbPath = GetOption(args, "--db") ?? Path.Combine(RepoRoot, "faolan.db");
			var capturesDir = GetOption(args, "--captures") ?? Path.Combine(RepoRoot, "Captures");
			var arg = GetOption(args, "--report-arg");

			switch (report)
			{
				case "conversation-states":
					ConversationStates(LoadCaptures(capturesDir));
					return 0;
				case "anim":
					Anim(LoadCaptures(capturesDir), arg ?? "Casilda");
					return 0;
				case "equip":
					Equip(LoadCaptures(capturesDir), arg ?? "Casilda");
					return 0;
				case "snapshot":
					SnapshotScan(LoadCaptures(capturesDir));
					return 0;
				case "mapchange":
					MapChangeScan(LoadCaptures(capturesDir));
					return 0;
				case "opcodes":
					OpcodeScan(LoadCaptures(capturesDir));
					return 0;
				case "opcodes-csv":
					OpcodeCsv(LoadCaptures(capturesDir));
					return 0;
				case "subopcodes-csv":
					SubOpcodeCsv(LoadCaptures(capturesDir), arg);
					return 0;
				case "worldobj":
					await WorldObj(dbPath, arg ?? "hangingwoman");
					return 0;
				case "spell-fields":
					SpellFields(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg);
					return 0;
				case "rdb-assets":
					RdbAssets(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg, GetOption(args, "--out"), args.Contains("--dump-mesh"));
					return 0;
				case "rdb-skeleton":
					RdbSkeleton(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg ?? "14853");
					return 0;
				case "rdb-rig":
					RdbRig(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg ?? "", GetOption(args, "--out"), GetOption(args, "--skeleton"));
					return 0;
				case "rdb-search":
					RdbSearch(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg ?? "");
					return 0;
				case "rdb-id":
					RdbId(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg ?? "");
					return 0;
				case "rdb-dump":
					RdbDump(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", arg ?? "", GetOption(args, "--out"));
					return 0;
				case "item-template":
					ItemTemplate(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", dbPath, arg ?? "", GetOption(args, "--out"));
					return 0;
				case "ability-names":
					AbilityNames(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB", dbPath);
					return 0;
				case "rdb-types":
					RdbTypes(GetOption(args, "--rdb") ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB");
					return 0;
				default:
					await Console.Error.WriteLineAsync($"Unknown report '{report}'. Known: conversation-states, anim, worldobj, spell-fields, rdb-assets.");
					return 1;
			}
		}

		// Matrix CSV for ONE opcode (arg, e.g. "0x2000"; default 0x2000): one row per capture, one column per
		// (direction, sub-opcode) seen under that opcode, cell = packet count. This is the drill-down of opcodes-csv -
		// e.g. every S2C 0x2000 IIR type as its own column. High-cardinality opcodes (0x2008/0x2020 handles) will be wide.
		private static void SubOpcodeCsv(IReadOnlyList<NamedCapture> captures, string arg)
		{
			var op = string.IsNullOrWhiteSpace(arg)
				? 0x2000u
				: Convert.ToUInt32(arg.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);

			var perCap = new Dictionary<string, Dictionary<(bool Dl, uint Sub), int>>();
			var cols = new SortedSet<(uint Sub, bool Dl)>();
			foreach (var c in captures)
			{
				var counts = new Dictionary<(bool Dl, uint Sub), int>();
				foreach (var p in c.Packets)
				{
					if (p.Opcode != op)
						continue;
					counts[(p.IsDownload, p.SubOpcode)] = counts.GetValueOrDefault((p.IsDownload, p.SubOpcode)) + 1;
					cols.Add((p.SubOpcode, p.IsDownload));
				}
				perCap[c.Name] = counts;
			}

			var ordered = cols.ToList();
			static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

			Console.Error.WriteLine($"opcode 0x{op:X4}: {ordered.Count} distinct (dir,sub) columns across {captures.Count} captures");
			Console.WriteLine("capture," + string.Join(",", ordered.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Sub:X8}")));
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var counts = perCap[c.Name];
				Console.WriteLine(Csv(c.Name) + "," + string.Join(",", ordered.Select(x => counts.TryGetValue((x.Dl, x.Sub), out var n) ? n : 0)));
			}
		}

		// Matrix CSV of the same index: one row per capture, one column per (direction, opcode) - sub-opcode collapsed
		// so the column set stays sane (~122) - cell = packet count. Import into a spreadsheet to pivot/filter dumps.
		private static void OpcodeCsv(IReadOnlyList<NamedCapture> captures)
		{
			var perCap = new Dictionary<string, Dictionary<(bool Dl, uint Op), int>>();
			var cols = new SortedSet<(uint Op, bool Dl)>(); // orders by opcode, then C2S before S2C
			foreach (var c in captures)
			{
				var counts = new Dictionary<(bool Dl, uint Op), int>();
				foreach (var p in c.Packets)
				{
					counts[(p.IsDownload, p.Opcode)] = counts.GetValueOrDefault((p.IsDownload, p.Opcode)) + 1;
					cols.Add((p.Opcode, p.IsDownload));
				}
				perCap[c.Name] = counts;
			}

			var ordered = cols.ToList();
			static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

			Console.WriteLine("capture," + string.Join(",", ordered.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Op:X4}")));
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var counts = perCap[c.Name];
				Console.WriteLine(Csv(c.Name) + "," + string.Join(",", ordered.Select(x => counts.TryGetValue((x.Dl, x.Op), out var n) ? n : 0)));
			}
		}

		// Grep-friendly index of every (direction, opcode, sub-opcode) tuple across the captures: per-dump counts, then
		// a global catalog (total + how many dumps carry it). Lets you find which dump to open for a given (sub)opcode.
		private static void OpcodeScan(IReadOnlyList<NamedCapture> captures)
		{
			var globalCount = new Dictionary<(bool Dl, uint Op, uint Sub), int>();
			var globalDumps = new Dictionary<(bool Dl, uint Op, uint Sub), HashSet<string>>();
			var totalPackets = 0;

			Console.WriteLine($"# Opcode index - {captures.Count} dumps");
			Console.WriteLine();
			Console.WriteLine("## Per dump");
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var counts = new Dictionary<(bool Dl, uint Op, uint Sub), int>();
				foreach (var p in c.Packets)
				{
					var k = (p.IsDownload, p.Opcode, p.SubOpcode);
					counts[k] = counts.GetValueOrDefault(k) + 1;
					globalCount[k] = globalCount.GetValueOrDefault(k) + 1;
					(globalDumps.TryGetValue(k, out var s) ? s : globalDumps[k] = []).Add(c.Name);
					totalPackets++;
				}

				Console.WriteLine($"### {c.Name}  ({c.Packets.Count} packets)");
				foreach (var (k, n) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Op).ThenBy(kv => kv.Key.Sub))
					Console.WriteLine($"  {(k.Dl ? "S2C" : "C2S")} op=0x{k.Op:X4} sub=0x{k.Sub:X8}  {n}");
				Console.WriteLine();
			}

			var byOp = globalCount
				.GroupBy(kv => (kv.Key.Dl, kv.Key.Op))
				.Select(g => (g.Key, Total: g.Sum(x => x.Value), Subs: g.Count(), Dumps: g.SelectMany(x => globalDumps[x.Key]).Distinct().Count()))
				.OrderBy(x => x.Key.Op).ThenBy(x => x.Key.Dl)
				.ToList();
			Console.WriteLine($"## Opcodes (sub-opcode collapsed) - {byOp.Count} distinct (dir,op)");
			foreach (var x in byOp)
				Console.WriteLine($"{(x.Key.Dl ? "S2C" : "C2S")} op=0x{x.Key.Op:X4}  total={x.Total}  {x.Subs} subs  in {x.Dumps} dumps");
			Console.WriteLine();

			Console.WriteLine($"## Global (sub)opcode catalog - {totalPackets} packets, {globalCount.Count} distinct (dir,op,sub) tuples");
			foreach (var (k, count) in globalCount.OrderBy(kv => kv.Key.Op).ThenBy(kv => kv.Key.Sub).ThenBy(kv => kv.Key.Dl))
				Console.WriteLine($"{(k.Dl ? "S2C" : "C2S")} op=0x{k.Op:X4} sub=0x{k.Sub:X8}  total={count}  in {globalDumps[k].Count} dumps");
		}

		// One 0x5D85BFC7 MapChange body per distinct map across the captures, so the map-build constants can be diffed
		// against BuildMapChange's hardcoded (beach) values - to see whether they're per-map or universal.
		private static void MapChangeScan(IReadOnlyList<NamedCapture> captures)
		{
			// Every MapChange body per map, with the session-dynamic fields masked (instance@42, token@58, token@75), so
			// we can tell whether the map-build constants are CONSISTENT per map (1 distinct masked body across all its
			// captures) or still vary by session - the gate for whether a per-map stored template is sound.
			var byMap = new Dictionary<uint, List<(string Cap, string Masked)>>();
			foreach (var c in captures)
			foreach (var p in c.Packets)
			{
				if (p.Opcode != 0x2000u || p.SubOpcode != 0x5D85BFC7u || p.Bytes.Length < 16)
					continue;
				var body = p.Bytes[(12 + (int)BinaryPrimitives.ReadUInt32BigEndian(p.Bytes.AsSpan(8, 4)))..];
				if (body.Length < 79)
					continue;
				var map = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
				var masked = (byte[])body.Clone();
				Array.Clear(masked, 42, 4); // instance
				Array.Clear(masked, 58, 4); // playfield token
				Array.Clear(masked, 75, 4); // playfield token (2nd)
				(byMap.TryGetValue(map, out var l) ? l : byMap[map] = []).Add((c.Name, Convert.ToHexString(masked)));
			}

			foreach (var (map, list) in byMap.OrderBy(kv => kv.Key))
			{
				var distinct = list.Select(x => x.Masked).Distinct().ToList();
				Console.WriteLine($"map=0x{map:X8}  {list.Count} caps  {distinct.Count} distinct masked  {(distinct.Count == 1 ? "CONSISTENT" : "*** VARIES ***")}");
				if (distinct.Count > 1)
					foreach (var d in distinct)
						Console.WriteLine($"    {d}  <- {string.Join(", ", list.Where(x => x.Masked == d).Select(x => x.Cap[..Math.Min(34, x.Cap.Length)]).Distinct().Take(4))}");
			}
		}

		// The biggest 0x737A6DF9 FullChar snapshots across the captures: size, instance, and the byte offsets where
		// the 0x737A6DF9 subop recurs in the body (>1 => the "snapshot" is several records concatenated, not one).
		private static void SnapshotScan(IReadOnlyList<NamedCapture> captures)
		{
			var snaps = new List<(string Cap, byte[] Body)>();
			foreach (var c in captures)
			foreach (var p in c.Packets)
			{
				if (!p.IsDownload || p.SubOpcode != 0x737A6DF9u)
					continue;
				var body = p.Bytes.Length >= 16 ? p.Bytes[(12 + (int)BinaryPrimitives.ReadUInt32BigEndian(p.Bytes.AsSpan(8, 4)))..] : [];
				if (body.Length >= 16)
					snaps.Add((c.Name, body));
			}

			foreach (var (cap, body) in snaps.OrderByDescending(s => s.Body.Length).Take(6))
			{
				var positions = new List<int>();
				for (var i = 0; i + 4 <= body.Length; i++)
					if (body[i] == 0x73 && body[i + 1] == 0x7A && body[i + 2] == 0x6D && body[i + 3] == 0xF9)
						positions.Add(i);
				var instance = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
				Console.WriteLine($"{cap[..Math.Min(46, cap.Length)],-46} size={body.Length,8} instance=0x{instance:X8} 0x737A6DF9@[{string.Join(",", positions)}]");
				Console.WriteLine($"    first48: {Convert.ToHexString(body.AsSpan(0, Math.Min(48, body.Length)))}");

				try
				{
					var s = new Faolan.Core.FaolanStream(body);
					s.ReadUInt32();
					s.ReadUInt32();
					s.ReadUInt32();
					s.ReadUInt32();
					var rootMsg = Faolan.GameServer.Packets.FullCharSnapshot.InventorySnapshotInspector.ParseInventoryRoot(s);
					Console.WriteLine($"    inventory-root: OK ({rootMsg.Fields.Count} fields)");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"    inventory-root: FAIL - {ex.GetType().Name}: {ex.Message}");
				}
			}
		}

		// Per capture, the named NPC's persistent idle animation (CharacterBcc.AnimIdleId) from its FA015807.
		private static void Anim(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
			foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
			{
				var body = e.Data is { Length: >= 12 }
					? e.Data[(12 + (int)BinaryPrimitives.ReadUInt32BigEndian(e.Data.AsSpan(8, 4)))..]
					: null;
				var anim = body != null && PlayerSpawnReader.TryReadCharacterMessage(body, out var read)
					? read.Message?.Bcc?.AnimIdleId.ToString() ?? "(no bcc)"
					: "(unreadable)";
				Console.WriteLine($"{c.Name[..Math.Min(40, c.Name.Length)]} | handle 0x{e.Handle:X} | AnimIdleId={anim} | pos=({e.X:0.#},{e.Z:0.#})");
			}
		}


		// Per-NPC dialogue screens + mid-conversation quest grants per (capture, NPC) (#37).
		private static void ConversationStates(IReadOnlyList<NamedCapture> captures)
		{
			foreach (var c in captures)
			foreach (var g in ConversationExtractor.Extract(c.Packets).Where(e => !string.IsNullOrEmpty(e.NpcName)).GroupBy(e => e.NpcName).OrderBy(g => g.Key))
			{
				var grants = g.Where(e => e.GrantQuestId != 0).OrderBy(e => e.AnswerPath).Select(e => $"'{e.AnswerPath}'->{e.GrantQuestId}");
				Console.WriteLine($"{c.Name} | {g.Key} | screens={g.Count()} | grants=[{string.Join(", ", grants)}]");
			}
		}

		private static string Describe(RdbAssetExtractor.ResourceResult? r)
		{
			return r != null
				? $"id={r.Id} type={r.Type} {r.Availability} ({r.Size} B)"
				: "(none)";
		}

		// Per capture, the named NPC's worn equipment (InventoryEntries: slot + template) from its FA015807 (#267).
		private static void Equip(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
			foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
			{
				var body = e.Data is { Length: >= 12 }
					? e.Data[(12 + (int)BinaryPrimitives.ReadUInt32BigEndian(e.Data.AsSpan(8, 4)))..]
					: null;
				if (body == null || !PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				{
					Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} | (unreadable)");
					continue;
				}

				var slots = read
					.Message
					.InventoryEntries
					.Select(entry => NpcSpawnEquipmentBuilder.TryLift(entry, out var row)
						? $"slot{row.Pos}=tpl{row.Template}[k={row.Key1},{row.Key2},{row.Key3},{row.Key4} lvl={row.Level} hash={row.Hash}]"
						: "slot?=unliftable")
					.ToList();
				Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} pos=({e.X:0.#},{e.Z:0.#})");
				foreach (var s in slots)
					Console.WriteLine($"    {s}");
			}
		}

		private static string? GetOption(string[] args, string name)
		{
			var i = Array.IndexOf(args, name);
			return i >= 0 && i + 1 < args.Length
				? args[i + 1]
				: null;
		}

		// Decrypts each Ability's RDB 1000020 entry with its stored Data1..4 TEA key (same scheme as item templates, #137)
		// and dumps the printable strings - the ability NAME lives in that (encrypted-but-key-available) template.
		private static void AbilityNames(string rdbDir, string dbPath)
		{
			using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={dbPath}").Options);
			using var rdb = new RdbReader(rdbDir);
			foreach (var a in db.Abilities.OrderBy(x => x.Slot).ToList())
			{
				var key = new[] { a.Data1, a.Data2, a.Data3, a.Data4 };
				if (!rdb.TryGet(1000020, a.Id, out var e) || e.File == 0xff || e.Size == 0)
				{
					Console.WriteLine($"0x{a.Id:X8} slot={a.Slot,-5} (no 1000020 entry)");
					continue;
				}

				byte[] body;
				try
				{
					body = rdb.ReadDecrypted(e, key);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"0x{a.Id:X8} (decrypt failed: {ex.Message})");
					continue;
				}

				var name = RdbItemImporter.ExtractName(body);
				var fields = ReadIncreasingFields(body.AsSpan(12));
				var fieldStr = string.Join(" ", fields.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));
				Console.WriteLine($"0x{a.Id:X8} slot={a.Slot,-5} \"{name}\"  | {fieldStr}");
			}
		}

		private static void ItemTemplate(string rdbDir, string dbPath, string arg, string outDir = null)
		{
			if (!uint.TryParse(arg, out var itemId))
			{
				Console.WriteLine($"item-template needs a numeric item id in --report-arg, got '{arg}'");
				return;
			}

			using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={dbPath}").Options);
			var item = db.Items.FirstOrDefault(i => i.Id == itemId);
			if (item == null)
			{
				Console.WriteLine($"item {itemId} not in DB");
				return;
			}

			Console.WriteLine($"item {itemId} \"{item.Name}\" type={item.Type} cat={item.SearchCategory} key={item.Data1:X8},{item.Data2:X8},{item.Data3:X8},{item.Data4:X8}");
			using var rdb = new RdbReader(rdbDir);
			var key = new[]
			{
				item.Data1,
				item.Data2,
				item.Data3,
				item.Data4
			};
			byte[] template = null;
			foreach (var type in new uint[]
			{
				1000020,
				1000620
			})
			{
				if (rdb.TryGet(type, itemId, out var e) && e.File != 0xff && e.Size > 0)
				{
					try
					{
						template = rdb.ReadDecrypted(e, key);
						Console.WriteLine($"decrypted type={type} {template.Length} B");
						break;
					}
					catch (Exception ex)
					{
						Console.WriteLine($"decrypt type={type} failed: {ex.Message}");
					}
				}
			}

			if (template == null)
			{
				Console.WriteLine("no template entry");
				return;
			}

			if (outDir != null)
			{
				Directory.CreateDirectory(outDir);
				File.WriteAllBytes(Path.Combine(outDir, $"item_{itemId}.bin"), template);
			}

			var body = template.AsSpan(12);

			// Every printable ASCII run >= 3 chars (asset/mesh names look like "armor_..._body" / "*bodymesh_NNN").
			Console.WriteLine("-- strings --");
			for (var i = 0; i < body.Length;)
			{
				var j = i;
				while (j < body.Length && body[j] >= 32 && body[j] <= 126)
					j++;
				if (j - i >= 3)
					Console.WriteLine($"  @{i,5} {Encoding.ASCII.GetString(body.Slice(i, j - i))}");
				i = j < body.Length
					? j + 1
					: j;
			}

			// Property table: the longest run of [fieldId][value] u32 pairs with strictly-decreasing fieldId in (0,2000].
			Console.WriteLine("-- property table (fieldId=value) --");
			var words = body.Length / 4;
			var bestStart = -1;
			var bestLen = 0;
			for (var start = 0; start + 1 < words; start++)
			{
				uint last = 0;
				var have = false;
				var len = 0;
				for (var j = start; j + 1 < words; j += 2)
				{
					var fid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j * 4, 4));
					if (fid == 0 || fid > 2000 || (have && fid >= last))
						break;

					last = fid;
					have = true;
					len++;
				}

				if (len > bestLen)
				{
					bestLen = len;
					bestStart = start;
				}
			}

			if (bestStart >= 0)
			{
				uint last = 0;
				var have = false;
				for (var j = bestStart; j + 1 < words; j += 2)
				{
					var fid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j * 4, 4));
					if (fid == 0 || fid > 2000 || (have && fid >= last))
						break;

					var val = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice((j + 1) * 4, 4));
					var resolves = "";
					foreach (var t in new uint[]
					{
						1010030,
						1010201,
						1010001,
						1010200,
						1000700,
						1010700
					})
					{
						if (val > 1000 && rdb.TryGet(t, val, out var re) && re.Size > 0)
							resolves += $" [->{t} {(re.File == 0xff ? "CDN" : "loc")}]";
					}

					Console.WriteLine($"  field {fid,4} = {val}{resolves}");
					last = fid;
					have = true;
				}
			}

			// Every u32 that resolves to a known character-asset RDB entry - candidate worn-mesh references.
			Console.WriteLine("-- u32 -> RDB asset refs --");
			var refTypes = new uint[]
			{
				1010030,
				1010201,
				1010001,
				1010200
			};
			for (var i = 0; i + 4 <= body.Length; i += 4)
			{
				var v = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i, 4));
				if (v < 1000)
					continue;

				foreach (var t in refTypes)
				{
					if (rdb.TryGet(t, v, out var re) && re.Size > 0)
						Console.WriteLine($"  @{i,5} {v} -> type {t} ({(re.File == 0xff ? "CDN" : "local")} {re.Size} B)");
				}
			}
		}

		private static List<NamedCapture> LoadCaptures(string capturesDir)
		{
			var captures = new List<NamedCapture>();
			foreach (var ts in Directory.EnumerateFiles(capturesDir, "*.tshark.json"))
			{
				var name = Path.GetFileName(ts);
				if (name.EndsWith(".tshark.json", StringComparison.OrdinalIgnoreCase))
					name = name[..^".tshark.json".Length];
				captures.Add(new NamedCapture(name, TsharkCaptureReader.Read(ts)));
			}

			return captures;
		}

		private static DatabaseContext OpenDb(string dbPath)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={dbPath}", x => x.MigrationsAssembly("Faolan")).Options;
			return new DatabaseContext(options);
		}

		// RDB -> 3D-model asset graph (Gitea #275, Phase 0). With no --report-arg: a corpus summary (how many
		// MESHINDEXFILE manifests resolve to geometry, how their textures are split local vs CDN). With --report-arg
		// <meshIndexId>: print that model's graph (RenderMesh id, materials, texture refs) and, if --out <dir> is given,
		// extract its FCTX textures to DDS (+ --dump-mesh to also write the raw 1010001 geometry blob for the Phase 1 spike).
		private static void RdbAssets(string rdbDir, string arg, string? outDir, bool dumpMesh)
		{
			using var rdb = new RdbReader(rdbDir);

			// Comma-separated ids => assemble them into one glb (character body + head + equipment).
			if (arg != null && arg.Contains(','))
			{
				var ids = arg
					.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(s => uint.TryParse(s, out var u)
						? u
						: 0u)
					.Where(u => u != 0)
					.ToList();
				var outPath = Path.Combine(outDir ?? ".", "assembled.glb");
				var res = RdbAssetExtractor.Assemble(rdb, ids, outPath);
				Console.WriteLine($"assembled {res.Used.Count}/{ids.Count} meshes: {res.Vertices} verts {res.Triangles} tris -> {outPath}");
				return;
			}

			if (uint.TryParse(arg, out var meshIndexId))
			{
				var result = RdbAssetExtractor.Extract(rdb, meshIndexId, outDir, dumpMesh);
				if (!result.Ok || result.Manifest is not { } m)
				{
					Console.WriteLine($"MESHINDEXFILE {meshIndexId} not found in RDB.");
					return;
				}

				Console.WriteLine($"MESHINDEXFILE {meshIndexId}");
				Console.WriteLine($"  source       : {m.Source}");
				Console.WriteLine($"  bounds       : {m.Bounds}");
				Console.WriteLine($"  renderMesh   : {Describe(m.RenderMesh)}{(m.RenderMeshDump != null ? $" -> {m.RenderMeshDump}" : "")}");
				if (m.Geometry is { } g)
					Console.WriteLine($"  geometry     : {g.Vertices} verts {g.Triangles} tris{(m.Glb != null ? $" -> {m.Glb}" : "")}");
				Console.WriteLine($"  collisionMesh: {Describe(m.CollisionMesh)}");
				Console.WriteLine($"  materials    : {m.Materials.Count}");
				foreach (var mat in m.Materials)
					Console.WriteLine($"    - {mat.Name}: [{string.Join(", ", mat.Textures)}]");
				Console.WriteLine($"  textures     : {m.Textures.Count}");
				foreach (var t in m.Textures)
					Console.WriteLine($"    - {t.Ref,-18} {t.Status}{(t.Output != null ? $" -> {t.Output}" : "")}");
				if (outDir != null)
					Console.WriteLine($"  written to   : {outDir}");
				return;
			}

			// Corpus summary.
			int manifests = 0, withGeometry = 0, geomLocal = 0, geomCdn = 0, texLocal = 0, texCdn = 0, texMissing = 0;
			foreach (var e in rdb.EntriesOfType(RdbAssetExtractor.MeshIndexFileType))
			{
				if (e.Size == 0 || !e.IsLocal)
					continue;

				manifests++;
				MeshIndexFile mesh;
				try
				{
					mesh = MeshIndexFile.Parse(e.Id, rdb.ReadDecompressed(e));
				}
				catch
				{
					continue;
				}

				if (mesh.RenderMeshId is { } rmId)
				{
					withGeometry++;
					if (rdb.TryGet(RdbAssetExtractor.RenderMeshType, rmId, out var rm))
					{
						(rm.IsLocal
							? ref geomLocal
							: ref geomCdn)++;
					}
				}

				foreach (var t in mesh.UniqueTextures)
				{
					if (!rdb.TryGet(t.Type!.Value, t.Id, out var te))
						texMissing++;
					else if (te.IsLocal)
						texLocal++;
					else
						texCdn++;
				}
			}

			Console.WriteLine($"MESHINDEXFILE manifests (local) : {manifests}");
			Console.WriteLine($"  with a RenderMesh ref         : {withGeometry} (geometry local={geomLocal} cdn={geomCdn})");
			Console.WriteLine($"  texture refs: local={texLocal} cdn={texCdn} missing={texMissing}");
			Console.WriteLine("Pass --report-arg <meshIndexId> [--out <dir>] [--dump-mesh] to extract one model.");
		}

		// List all entries of a TYPE (--report-arg <type>) and dump each decompressed payload to --out as
		// <type>_<id>.bin for offline inspection. Used to crack new RDB record formats (e.g. CHARACTERCREATOR 1010700).
		private static void RdbDump(string rdbDir, string arg, string? outDir)
		{
			if (!uint.TryParse(arg, out var type))
			{
				Console.WriteLine($"rdb-dump needs a numeric type in --report-arg, got '{arg}'");
				return;
			}

			using var rdb = new RdbReader(rdbDir);
			if (outDir != null)
				Directory.CreateDirectory(outDir);

			foreach (var e in rdb.EntriesOfType(type).Where(e => e.File != 0xff))
			{
				var body = rdb.ReadDecompressed(e);
				Console.WriteLine($"type={type} id={e.Id} decompressed={body.Length} B");
				if (outDir != null)
					File.WriteAllBytes(Path.Combine(outDir, $"{type}_{e.Id}.bin"), body);
			}
		}

		// List every (type, id) entry in the RDB index that has the given id, with availability. Used to discover what
		// id-space a cross-reference lives in (e.g. a character's HeadMesh / *hairmesh resource id) (#275).
		private static void RdbId(string rdbDir, string arg)
		{
			if (!uint.TryParse(arg, out var id))
			{
				Console.WriteLine($"rdb-id needs a numeric --report-arg, got '{arg}'");
				return;
			}

			using var rdb = new RdbReader(rdbDir);
			var hits = rdb.Entries.Where(e => e.Id == id).OrderBy(e => e.Type).ToList();
			Console.WriteLine($"id {id}: {hits.Count} entr(ies)");
			foreach (var e in hits)
				Console.WriteLine($"  type={e.Type,-9} {(e.File == 0xff ? "CDN " : "local")} size={e.Size}");
		}

		// Decode a skeleton (RDB type 1010002) and print key bone world positions (#275 Phase 3 skeleton validation).
		// Emit a SKINNED glb for a skinned model (body/equipment): geometry + the character skeleton as a glTF skin.
		// --report-arg <meshIndexId>  --out <dir>  [--skeleton <id>, default 14853].
		private static void RdbRig(string rdbDir, string arg, string? outDir, string? skelArg)
		{
			using var rdb = new RdbReader(rdbDir);
			if (!uint.TryParse(arg, out var meshIndexId))
			{
				Console.WriteLine($"rdb-rig needs a numeric MESHINDEXFILE id in --report-arg, got '{arg}'");
				return;
			}

			uint? skeletonId = uint.TryParse(skelArg, out var s)
				? s
				: null; // null => auto-select by bone palette
			var glb = RdbAssetExtractor.BuildSkinnedModelGlb(rdb, meshIndexId, skeletonId);
			if (glb == null)
			{
				Console.WriteLine($"meshindex {meshIndexId} is not a readable skinned model (or no matching skeleton).");
				return;
			}

			Console.WriteLine($"skinned glb: meshindex={meshIndexId} skeleton={skeletonId?.ToString() ?? "auto"} bytes={glb.Length}");
			if (outDir != null)
			{
				Directory.CreateDirectory(outDir);
				var path = Path.Combine(outDir, $"rig_{meshIndexId}.glb");
				File.WriteAllBytes(path, glb);
				Console.WriteLine($"  -> {path}");
			}
		}

		// Scan every MESHINDEXFILE (type 1010030) manifest and print those whose .max source path contains the given
		// substring (case-insensitive). Used to locate race/sex character meshes (head/body/etc.) by name (#275).
		private static void RdbSearch(string rdbDir, string needle)
		{
			using var rdb = new RdbReader(rdbDir);
			needle = needle.ToLowerInvariant();
			var hits = 0;
			foreach (var e in rdb.EntriesOfType(RdbAssetExtractor.MeshIndexFileType))
			{
				if (e.Size == 0)
					continue;

				MeshIndexFile mesh;
				try
				{
					mesh = MeshIndexFile.Parse(e.Id, rdb.ReadDecompressed(e));
				}
				catch
				{
					continue;
				}

				var src = mesh.SourceFile;
				if (src == null || !src.ToLowerInvariant().Contains(needle))
					continue;

				var rm = mesh.RenderMeshId is { } rmId && rdb.TryGet(RdbAssetExtractor.RenderMeshType, rmId, out var rmE)
					? $"rm={rmId} {(rmE.File == 0xff ? "CDN" : "local")}"
					: "rm=(none)";
				Console.WriteLine($"meshIndex={e.Id,-9} skinned={mesh.Skinned,-5} {rm,-14} {src}");
				hits++;
			}

			Console.WriteLine($"-- {hits} manifest(s) matched '{needle}'.");
		}

		private static void RdbSkeleton(string rdbDir, string arg)
		{
			using var rdb = new RdbReader(rdbDir);
			if (!uint.TryParse(arg, out var id) || !rdb.TryGet(1010002, id, out var e))
			{
				Console.WriteLine($"skeleton {arg} not found");
				return;
			}

			var bones = SkeletonReader.Read(rdb.ReadDecompressed(e));
			Console.WriteLine($"skeleton {id}: {bones.Count} bones");
			foreach (var b in bones)
			{
				if (b.Name is "rig_bone_Hips" or "rig_bone_Neck" or "rig_bone_Head" or "rig_bone_Spine2" or "rig_bone_LeftHand" or "rig_bone_LeftFoot")
				{
					var w = b.WorldPosition;
					Console.WriteLine($"  {b.Name,-22} world=({w.X:0.000}, {w.Y:0.000}, {w.Z:0.000})");
				}
			}
		}

		// Decrypt one item's RDB template (type 1000020/1000620) with its stored Data1..4 TEA key and surface everything
		// that could be a worn-appearance mesh ref: every ASCII string, the property table, and every u32 that resolves
		// to a MESHINDEXFILE (1010030) / MonsterData (1010201) / RenderMesh (1010001) entry in the RDB (#275 gear).
		// type -> (entry count, local count) histogram of the whole RDB index, to spot candidate record families.
		private static void RdbTypes(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var hist = new Dictionary<uint, (int total, int local)>();
			foreach (var e in rdb.Entries)
			{
				var cur = hist.GetValueOrDefault(e.Type);
				hist[e.Type] = (cur.total + 1, cur.local + (e.File != 0xff
					? 1
					: 0));
			}

			foreach (var kv in hist.OrderByDescending(k => k.Value.total))
				Console.WriteLine($"type {kv.Key,-10} total={kv.Value.total,-8} local={kv.Value.local}");
		}

		// Mirror of RdbSpellImporter.ReadIncreasingFields (kept private there); used only by the spell-fields report.
		private static Dictionary<uint, uint> ReadIncreasingFields(ReadOnlySpan<byte> body)
		{
			var words = body.Length / 4;
			var best = new Dictionary<uint, uint>();
			for (var start = 0; start + 1 < words; start++)
			{
				uint last = 0;
				var haveLast = false;
				var cur = new Dictionary<uint, uint>();
				for (var j = start; j + 1 < words; j++)
				{
					var fid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j * 4, 4));
					if (fid == 0 || fid > 2000 || (haveLast && fid <= last))
						break;

					cur[fid] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice((j + 1) * 4, 4));
					last = fid;
					haveLast = true;
					j++;
				}

				if (cur.Count > best.Count)
					best = cur;
			}

			return best;
		}

		// TEMP (#274): discover the spell DURATION field in the RDB spell-data records (type 1040005). Dumps either a
		// specific spell's full increasing field table (--report-arg <decimal id>) or, with no arg, a histogram of how
		// often each field id appears across all named spell records (so a duration field stands out as near-universal
		// with ms-range values). Field 0x126=cast/0x11F=range/0x15C=damage are already known.
		private static void SpellFields(string rdbDir, string arg)
		{
			using var rdb = new RdbReader(rdbDir);
			var counts = new Dictionary<uint, int>();
			var samples = new Dictionary<uint, List<uint>>();
			uint? wanted = uint.TryParse(arg, out var w)
				? w
				: null;
			foreach (var e in rdb.EntriesOfType(1040005))
			{
				if (e.Size == 0 || e.File == 0xff)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 12)
					continue;

				var name = RdbItemImporter.ExtractName(body);
				if (string.IsNullOrEmpty(name))
					continue;

				var fields = ReadIncreasingFields(body.AsSpan(12));
				if (wanted is uint id && e.Id == id)
				{
					Console.WriteLine($"spell {e.Id} '{name}':");
					foreach (var kv in fields.OrderBy(k => k.Key))
						Console.WriteLine($"  0x{kv.Key:X3} ({kv.Key,4}) = {kv.Value}  (0x{kv.Value:X})");
					return;
				}

				foreach (var kv in fields)
				{
					counts[kv.Key] = counts.GetValueOrDefault(kv.Key) + 1;
					if (!samples.TryGetValue(kv.Key, out var l))
						samples[kv.Key] = l = new List<uint>();
					if (l.Count < 8)
						l.Add(kv.Value);
				}
			}

			if (wanted != null)
			{
				Console.WriteLine($"spell {wanted} not found / unnamed");
				return;
			}

			foreach (var kv in counts.OrderByDescending(k => k.Value).Take(40))
				Console.WriteLine($"0x{kv.Key:X3} ({kv.Key,4}) count={kv.Value,5}  samples={string.Join(",", samples[kv.Key])}");
		}

		// Build a named world object via the burstless world-entry path and print its hex (render diffing).
		private static async Task WorldObj(string dbPath, string target)
		{
			await using var db = OpenDb(dbPath);
			foreach (var row in await db.WorldObjects.Where(o => o.Name.Contains(target)).ToListAsync())
			{
				var baseOk = WorldObjectBuilder.TryBuild(row, row.CapturedHandle, out var baseBody);
				var rewriteOk = baseOk && SpawnPlayfieldRewriter.TryRewrite(baseBody, 0x01EA985Cu);
				Console.WriteLine($"map {row.MapId} {row.Name} handle 0x{row.CapturedHandle:X} baseBuild={baseOk} rewrite={rewriteOk} datalen={row.Data?.Length} kd={row.KdTreeTail?.Length}");
				if (baseOk)
					Console.WriteLine("  BUILT: " + Convert.ToHexString(baseBody).ToLowerInvariant());
			}
		}
	}
}
