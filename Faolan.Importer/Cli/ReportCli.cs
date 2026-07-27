using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Importer.Modules;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// Read-only diagnostics (the old --dump-*/--burst-coverage flags), routed through `--report <name>`.
	// These inspect captures or the DB and print; they never write, so they bypass the import runner entirely.
	public static partial class ReportCli
	{
		private static (FaolanDbProvider Provider, string Connection)? _db;

		// DB config (provider + connection) for the DB-backed reports. Resolved LAZILY on the first OpenDb so the
		// capture/RDB-only reports (corpus-extract, coverage-of-captures, ...) need no DB config at all. Same source
		// and precedence as the import path (env > cmdline(--Database:ConnectionString) > faolan.json) - see CliConfig.
		private static ImporterOptions _options = null!;

		public static async Task<int> RunAsync(ImporterOptions options, string report)
		{
			_options = options; // OpenDb resolves the DB connection from these on demand (CliConfig)
			var capturesDir = options.Captures;
			var arg = options.ReportArg;

			// The DB-only reports never touch the corpus, so they run before the captures check below.
			if (report == "sql-seed")
				return await SqlSeed(arg);

			if (report == "apply-seed")
				return ApplySeed();

			// RDB-only, no corpus: the client's stat-name table (LDB cat 615).
			if (report == "ldb-stat-names")
			{
				LdbStatNames(RdbDir(), options.Out);
				return 0;
			}

			// Every report below reads the corpus. It is NOT in this repo (private ProjectFaolan-Data), so say where
			// to point rather than letting Directory.EnumerateFiles throw a bare path error.
			if (!Directory.Exists(capturesDir))
			{
				await Console.Error.WriteLineAsync($"Captures directory not found: {capturesDir}");
				await Console.Error.WriteLineAsync("  The corpus is NOT in this repo - it lives in the private ProjectFaolan-Data repo");
				await Console.Error.WriteLineAsync("  (see CLAUDE.md \"Capture data\"). Pass --captures <dir> pointing at its Captures/ folder.");
				return 1;
			}

			switch (report)
			{
				case "corpus-extract":
				{
					// The test suite's slice of the corpus, so a plain clone can run the whole-corpus gates without
					// the 349 MB capture repo. See CorpusExtractWriter.
					// Default paths here are CWD-relative: the importer runs from the repo root (see CLAUDE.md).
					var target = arg ?? Path.Combine("Faolan.Tests", "Assets", "corpus-extract.bin");
					CorpusExtractWriter.Write(capturesDir, target);
					return 0;
				}

				case "conversation-states":
				{
					ConversationStates(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "coverage":
				{
					await Coverage(NamedCapture.LoadAll(capturesDir), arg, RdbDir());
					return 0;
				}

				case "anim":
				{
					Anim(NamedCapture.LoadAll(capturesDir), arg ?? "Casilda");
					return 0;
				}

				case "equip":
				{
					Equip(NamedCapture.LoadAll(capturesDir), arg ?? "Casilda");
					return 0;
				}

				case "snapshot":
				{
					SnapshotScan(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "mapchange":
				{
					MapChangeScan(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "opcodes":
				{
					OpcodeScan(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "opcodes-csv":
				{
					OpcodeCsv(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "subopcodes-csv":
				{
					SubOpcodeCsv(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "worldobj":
				{
					await WorldObj(arg ?? "hangingwoman");
					return 0;
				}

				case "spell-fields":
				{
					SpellFields(RdbDir(), arg);
					return 0;
				}

				case "rdb-assets":
				{
					RdbAssets(RdbDir(), arg, _options.Out, _options.DumpMesh);
					return 0;
				}

				case "rdb-skeleton":
				{
					RdbSkeleton(RdbDir(), arg ?? "14853");
					return 0;
				}

				case "rdb-rig":
				{
					RdbRig(RdbDir(), arg ?? "", _options.Out, _options.Skeleton);
					return 0;
				}

				case "rdb-search":
				{
					RdbSearch(RdbDir(), arg ?? "");
					return 0;
				}

				case "rdb-id":
				{
					RdbId(RdbDir(), arg ?? "");
					return 0;
				}

				case "rdb-dump":
				{
					RdbDump(RdbDir(), arg ?? "", _options.Out);
					return 0;
				}

				case "item-template":
				{
					ItemTemplate(RdbDir(), arg ?? "", _options.Out);
					return 0;
				}

				case "item-stat-scaling":
				{
					ItemStatScaling(RdbDir(), _options.Out);
					return 0;
				}

				case "class-attribute-runs":
				{
					ClassAttributeRuns(RdbDir(), _options.Out);
					return 0;
				}

				case "feat-catalog":
				{
					FeatCatalog(RdbDir(), arg, _options.Out);
					return 0;
				}

				case "rdb-class-vitals":
				{
					RdbClassVitals(RdbDir(), string.IsNullOrWhiteSpace(arg)
						? null
						: arg);
					return 0;
				}

				case "rdb-tweaks":
				{
					RdbTweaks(RdbDir(), string.IsNullOrWhiteSpace(arg)
						? null
						: arg);
					return 0;
				}

				case "ability-names":
				{
					AbilityNames(RdbDir());
					return 0;
				}

				case "rdb-types":
				{
					RdbTypes(RdbDir());
					return 0;
				}

				case "terrain-export":
				{
					await TerrainExport(RdbDir(), _options.TerrainOut ?? "Terrain");
					return 0;
				}

				case "spell-template":
				{
					var rdbDir = RdbDir();
					var ids = (arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
					using var rdb = new RdbReader(rdbDir);
					foreach (var idStr in ids)
					{
						var id = uint.Parse(idStr.Trim());
						if (!rdb.TryGet(1040005, id, out var e))
						{
							Console.WriteLine($"{id}: no 1040005 entry");
							continue;
						}

						var b = rdb.ReadDecompressed(e);
						// The [2][9] stat-modifier region - SAME parser as item templates. Body = the span after the
						// 12-byte RDB-Id header.
						var mods = ItemTemplateStatModifiers.Parse(b.AsSpan(12));
						var mag = 0u;
						var w = new uint[b.Length / 4];
						for (var i = 3; i + 1 < w.Length; i += 2)
						{
							var f = b.ReadU32Le(i * 4);
							if (f == 551)
								mag = b.ReadU32Le((i + 1) * 4);
						}

						var modStr = string.Join(", ", mods.Select(m => $"0x{m.StatId:x}={m.WireAmount}"));
						Console.WriteLine($"  {id}: mag(551)={mag}  region[{mods.Count}]: {modStr}");
					}

					return 0;
				}

				case "spell-stat-coverage":
				{
					var rdbDir = RdbDir();
					using var rdb = new RdbReader(rdbDir);
					int total = 0, withMods = 0, records = 0;
					var byStat = new Dictionary<uint, int>();
					foreach (var e in rdb.EntriesOfType(1040005).Where(x => x.IsLocal))
					{
						byte[] dec;
						try
						{
							dec = rdb.ReadDecompressed(e);
						}
						catch
						{
							continue;
						}

						if (dec.Length < 12)
							continue;

						total++;
						var mods = ItemTemplateStatModifiers.Parse(dec.AsSpan(12));
						if (mods.Count == 0)
							continue;

						withMods++;
						records += mods.Count;
						foreach (var m in mods)
							byStat[m.StatId] = byStat.GetValueOrDefault(m.StatId) + 1;
					}

					Console.WriteLine($"spells scanned={total}  with [2][9] stat mods={withMods}  total records={records}");
					foreach (var kv in byStat.OrderByDescending(k => k.Value).Take(30))
						Console.WriteLine($"  stat 0x{kv.Key:x} ({kv.Key}): {kv.Value} spells");
					return 0;
				}

				case "ldb-strings":
				{
					var rdbDir = RdbDir();
					var needle = (arg ?? "damage").ToLowerInvariant();
					using var rdb = new RdbReader(rdbDir);
					foreach (var e in rdb.EntriesOfType(1030002).Where(x => x.IsLocal))
					{
						byte[] dec;
						try
						{
							dec = rdb.ReadDecompressed(e);
						}
						catch
						{
							continue;
						}

						var sb = new StringBuilder();
						var runs = new List<string>();
						foreach (var b in dec)
						{
							if (b is >= 32 and < 127)
								sb.Append((char)b);
							else
							{
								if (sb.Length >= 3)
									runs.Add(sb.ToString());
								sb.Clear();
							}
						}

						if (sb.Length >= 3)
							runs.Add(sb.ToString());
						for (var i = 0; i < runs.Count; i++)
						{
							if (runs[i].ToLowerInvariant().Contains(needle))
								Console.WriteLine($"  {e.Id}[{i}]: {runs[i]}");
						}
					}

					return 0;
				}

				case "ldb-resolve":
				{
					// #291: walk the LDB's TDC2 pages (LdbStringIndex owns that grammar) and build a global id -> string
					// map, then print lookups for the pins + any --report-arg ids, to verify offline.
					var rdbDir = RdbDir();
					using var rdb = new RdbReader(rdbDir);
					var map = new Dictionary<uint, string>();
					var byCat = new Dictionary<(uint cat, uint id), string>();
					var perCategory = new Dictionary<uint, int>();
					// Per-page id->string for name categories, so we can crib-identify the ENGLISH page(s) against the
					// names our catalog already carries (#291/#300): 50210 spell names, 50200/50220 item names.
					var spellNamePages = new List<(uint page, Dictionary<uint, string> ids)>();
					var itemNamePages = new List<(uint cat, uint page, Dictionary<uint, string> ids)>();
					var pages = 0;
					foreach (var (pageId, category, strings) in LdbPagesOf(rdb))
					{
						pages++;
						foreach (var (id, str) in strings)
						{
							map[id] = str;
							byCat[(category, id)] = str;
						}

						perCategory[category] = perCategory.GetValueOrDefault(category) + strings.Count;

						if (strings.Count == 0)
							continue;

						if (category == LdbStringIndex.SpellNameCategory)
							spellNamePages.Add((pageId, strings));
						else if (category is LdbStringIndex.ItemNameCategory or 50220)
							itemNamePages.Add((category, pageId, strings));
					}

					Console.WriteLine($"LDB: {pages} pages parsed, {map.Count} unique ids, {perCategory.Count} categories.");
					// Per-category: count + an ASCII-ratio on a sample so English (high ASCII) is distinguishable from CJK.
					Console.WriteLine("=== categories (id, count, asciiPct, sample) ===");
					foreach (var (cat, cnt) in perCategory.OrderByDescending(kv => kv.Value).Take(30))
					{
						var sample = byCat.First(kv => kv.Key.cat == cat).Value;
						var ascii = sample.Length == 0
							? 0
							: sample.Count(c => c < 128) * 100 / sample.Length;
						Console.WriteLine($"  cat {cat,-8} n={cnt,-6} ascii={ascii,3}%  '{sample.Substring(0, Math.Min(40, sample.Length))}'");
					}

					// (No language histogram: language is per-PAGE, not per-entry - the index entry's 2nd u32 is
					// langOrFlags and the shared parser deliberately ignores it. The crib below is how a page's language
					// is actually determined.)
					await using (var db = OpenDb())
					{
						var spellNames = await db.Spells.Where(s => s.Name != null && s.Name != "").ToDictionaryAsync(s => s.Id, s => Norm(s.Name!));
						Console.WriteLine($"=== spell-name crib ({spellNamePages.Count} cat-50210 pages vs {spellNames.Count} named Spells) ===");
						var scored = spellNamePages
							.Select(p =>
							{
								int overlap = 0, match = 0;
								foreach (var (id, str) in p.ids)
								{
									if (spellNames.TryGetValue(id, out var known))
									{
										overlap++;
										if (Norm(str) == known)
											match++;
									}
								}

								return (p.page, p.ids.Count, overlap, match, pct: overlap == 0
									? 0
									: match * 100 / overlap);
							})
							.Where(x => x.overlap >= 5)
							.OrderByDescending(x => x.match)
							.ToList();
						var english = scored.Where(x => x.pct >= 80).ToList();
						foreach (var x in scored.Take(14))
							Console.WriteLine($"  page {x.page,-9} ids={x.Item2,-5} overlap={x.overlap,-5} match={x.match,-5} {x.pct,3}% {(x.pct >= 80 ? "<-- ENGLISH" : "")}");
						var englishNames = new Dictionary<uint, string>();
						foreach (var x in english)
							foreach (var (id, str) in spellNamePages.First(p => p.page == x.page).ids)
								englishNames[id] = str;
						var emptyNamed = await db.Spells.Where(s => s.Name == null || s.Name == "").Select(s => s.Id).ToListAsync();
						var fillable = emptyNamed.Where(englishNames.ContainsKey).ToList();
						Console.WriteLine(
							$"=== backfill preview: {english.Count} English pages, {englishNames.Count} English names; {emptyNamed.Count} empty-name Spells, {fillable.Count} fillable ===");
						foreach (var id in fillable.Take(20))
							Console.WriteLine($"  spell {id}: '{englishNames[id]}'");

						// Items: same crib, per candidate category (50200 dev-names, 50220). Which category is keyed by
						// item id AND English is what this tells us.
						var itemNames = await db.Items.Where(i => i.Name != null && i.Name != "").ToDictionaryAsync(i => i.Id, i => Norm(i.Name!));
						foreach (var cat in new uint[] { 50200, 50220 })
						{
							var catPages = itemNamePages.Where(p => p.cat == cat).ToList();
							Console.WriteLine($"=== item crib cat {cat} ({catPages.Count} pages vs {itemNames.Count} named Items) ===");
							var scoredI = catPages
								.Select(p =>
								{
									int overlap = 0, match = 0;
									foreach (var (id, str) in p.ids)
									{
										if (itemNames.TryGetValue(id, out var known))
										{
											overlap++;
											if (Norm(str) == known)
												match++;
										}
									}

									return (p.page, p.ids.Count, overlap, match, pct: overlap == 0
										? 0
										: match * 100 / overlap);
								})
								.Where(x => x.overlap >= 5)
								.OrderByDescending(x => x.match)
								.ToList();
							foreach (var x in scoredI.Take(8))
								Console.WriteLine($"  page {x.page,-9} ids={x.Item2,-6} overlap={x.overlap,-5} match={x.match,-5} {x.pct,3}% {(x.pct >= 80 ? "<-- ENGLISH" : "")}");
							var engI = new Dictionary<uint, string>();
							foreach (var x in scoredI.Where(x => x.pct >= 80))
								foreach (var (id, str) in catPages.First(p => p.page == x.page).ids)
									engI[id] = str;
							var emptyI = await db.Items.Where(i => i.Name == null || i.Name == "").Select(i => i.Id).ToListAsync();
							var fillI = emptyI.Where(engI.ContainsKey).ToList();
							Console.WriteLine($"    -> {engI.Count} English names; {emptyI.Count} empty-name Items, {fillI.Count} fillable");
							foreach (var id in fillI.Take(10))
								Console.WriteLine($"       item {id}: '{engI[id]}'");
						}
					}

					return 0;

					// #291/#300 crib: identify the ENGLISH spell-name page(s) by scoring each cat-50210 page against the
					// English names our Spells catalog already carries (from the template ExtractName). The page whose
					// strings match our names is English; the FR/DE/ES/RU/PL/KO copies of the same ids won't.
					static string Norm(string s)
					{
						return s.Trim().ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
					}
				}

				default:
				{
					await Console.Error.WriteLineAsync($"Unknown report '{report}'. Known: coverage, conversation-states, anim, worldobj, spell-fields, rdb-assets, ldb-strings, ldb-resolve.");
					return 1;
				}
			}
		}

		// Decrypts each Ability's RDB 1000020 entry with its stored Data1..4 TEA key (same scheme as item templates, #137)
		// and dumps the printable strings - the ability NAME lives in that (encrypted-but-key-available) template.
		private static void AbilityNames(string rdbDir)
		{
			using var db = OpenDb();
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			foreach (var a in db.Abilities.OrderBy(x => x.Slot).Include(ability => ability.Key).ToList())
			{
				var key = new[] { a.Key.Key1, a.Key.Key2, a.Key.Key3, a.Key.Key4 };
				if (!rdb.TryGet(1000020, a.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
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

		// Per capture, the named NPC's persistent idle animation (CharacterBcc.AnimIdleId) from its FA015807.
		private static void Anim(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
				{
					var body = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
						: null;
					var anim = body != null && PlayerSpawnReader.TryReadCharacterMessage(body, out var read)
						? read.Message?.Bcc?.AnimIdleId.ToString() ?? "(no bcc)"
						: "(unreadable)";
					Console.WriteLine($"{c.Name[..Math.Min(40, c.Name.Length)]} | handle 0x{e.Handle:X} | AnimIdleId={anim} | pos=({e.X:0.#},{e.Z:0.#})");
				}
		}

		private static void ConversationStates(IReadOnlyList<NamedCapture> captures)
		{
			foreach (var c in captures)
				foreach (var g in ConversationExtractor.Extract(c.Packets).Where(e => !string.IsNullOrEmpty(e.NpcName)).GroupBy(e => e.NpcName).OrderBy(g => g.Key))
				{
					var grants = g.Where(e => e.GrantQuestId != 0).OrderBy(e => e.AnswerPath).Select(e => $"'{e.AnswerPath}'->{e.GrantQuestId}");
					Console.WriteLine($"{c.Name} | {g.Key} | screens={g.Count()} | grants=[{string.Join(", ", grants)}]");
				}
		}

		// Per-NPC dialogue screens + mid-conversation quest grants per (capture, NPC) (#37).
		// Content coverage: of the NPCs/vendors we've actually INTERACTED with across the capture corpus, how many did
		// we import. Capture-relative by necessity (there is no authoritative game-wide vendor/dialogue list - retail
		// dialogue is TEA-encrypted in the RDB), so it measures import completeness OF WHAT WE'VE SEEN and names the
		// gaps to go capture/decode. `--report-arg readme` rewrites the marked section of README.md; else it prints.
		private static async Task Coverage(IReadOnlyList<NamedCapture> captures, string? arg, string? rdbDir)
		{
			// Conversations: greeted NPCs (denominator) vs imported ConversationNodes (numerator).
			var greeted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var c in captures)
				foreach (var n in ConversationExtractor.GreetedNpcNames(c.Packets))
					greeted.Add(n);

			// Shops: vendors whose 0x2033 window appears in a dump (denominator) vs imported Shop rows (numerator).
			var vendorsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var c in captures)
				foreach (var e in CaptureShopImporter.Extract(c.Packets))
					vendorsSeen.Add(e.VendorName);

			using var db = OpenDb();
			var convImported = (await db.ConversationNodes.Select(n => n.NpcName).Distinct().ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var shopImported = (await db.Shops.Select(s => s.VendorName).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

			// A greeted VENDOR opens its shop, not a conversation - exclude vendors (imported shops) from the dialogue
			// denominator so they don't read as conversation gaps. A real conversation gap = greeted, but neither a
			// dialogue node NOR a shop was imported for it (it falls to the "no dialogue or shop imported" bubble).
			var greetedTalkers = greeted.Where(n => !shopImported.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var convGaps = greetedTalkers.Where(n => !convImported.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
			var shopGaps = vendorsSeen.Where(v => !shopImported.Contains(v)).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
			var convHave = greetedTalkers.Count - convGaps.Count;
			var shopHave = vendorsSeen.Count - shopGaps.Count;

			var block = new StringBuilder();
			block.AppendLine($"- **NPC conversations** — {convHave}/{greetedTalkers.Count} greeted dialogue NPCs imported ({Pct(convHave, greetedTalkers.Count)})");
			block.AppendLine($"- **Vendor shops** — {shopHave}/{vendorsSeen.Count} captured vendors imported ({Pct(shopHave, vendorsSeen.Count)})");

			// RDB item/feat/ability template decryption (optional - needs the client RDB). The whole catalog (type
			// 1000020) is TEA-encrypted; we can only decrypt an entry whose key we've harvested from a capture (item
			// quads via ItemHashHarvester, ability keys via the 0x2052 grants). There is NO plaintext category tag in
			// the encrypted RDBH header, so a per-category %-of-total is impossible (we can't tell an undecrypted
			// entry's category) - we report the overall decrypted share + the absolute per-category decrypted counts.
			var rdb = string.IsNullOrEmpty(rdbDir)
				? RdbDir()
				: rdbDir;
			if (!string.IsNullOrEmpty(rdb) && Directory.Exists(rdb))
			{
				var itemIds = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();
				var abilityIds = (await db.Abilities.Select(a => a.Id).ToListAsync()).ToHashSet();
				using var reader = new RdbReader(rdb);
				var catalog = reader.EntriesOfType(1000020).ToList();
				int encrypted = 0, plaintext = 0, decrypted = 0, itemsDec = 0, abilitiesDec = 0;
				foreach (var e in catalog)
				{
					byte[] blob;
					try
					{
						blob = reader.ReadDecompressed(e);
					}
					catch
					{
						continue;
					}

					if (RdbReader.IsRdbh(blob))
					{
						encrypted++;
						if (itemIds.Contains(e.Id))
						{
							decrypted++;
							itemsDec++;
						}
						else if (abilityIds.Contains(e.Id))
						{
							decrypted++;
							abilitiesDec++;
						}
					}
					else
						plaintext++;
				}

				block.AppendLine(
					$"- **RDB template catalog** (type 1000020, TEA-encrypted) — {decrypted:N0}/{encrypted:N0} decrypted, **{Pct(decrypted, encrypted)} of the total encrypted**. Keys held per category: {itemsDec:N0} items, {abilitiesDec:N0} abilities, 0 feats (feat keys absent from captures). (+{plaintext:N0} plaintext catalog entries, no key needed.)");
			}

			Console.WriteLine("Content coverage (capture-relative: of the NPCs/vendors seen in dumps, how many are imported)\n");
			Console.Write(block.ToString());
			if (convGaps.Count > 0)
				Console.WriteLine($"\nconversation gaps ({convGaps.Count}) - greeted in a dump, no dialogue imported:\n  {string.Join(", ", convGaps)}");
			if (shopGaps.Count > 0)
				Console.WriteLine($"\nshop gaps ({shopGaps.Count}) - a vendor window is in a dump, no Shop row:\n  {string.Join(", ", shopGaps)}");

			// Imported-content stats: the scale of the DB (the server's source of truth). One `·`-separated line.
			var stats = $"**Imported content** (the DB is the server's source of truth): " +
						$"{await db.Maps.CountAsync():N0} maps · {await db.NpcTemplates.CountAsync():N0} NPC types / {await db.NpcSpawns.CountAsync():N0} placements · " +
						$"{await db.Items.CountAsync():N0} items · {await db.Spells.CountAsync():N0} spells · {await db.Abilities.CountAsync():N0} abilities · " +
						$"{await db.ConversationNodes.CountAsync():N0} conversation nodes · {await db.Shops.CountAsync():N0} shops · {await db.QuestDefinitions.CountAsync():N0} quests · " +
						$"{await db.WorldObjects.CountAsync():N0} world objects · {await db.PatrolNodes.Where(n => n.ParentPath == null).CountAsync():N0} patrol routes";
			Console.WriteLine("\n" + stats);

			if (string.Equals(arg, "readme", StringComparison.OrdinalIgnoreCase))
			{
				UpdateReadmeSection("coverage", block.ToString().TrimEnd());
				UpdateReadmeSection("stats", stats);
				Console.WriteLine("\nREADME.md coverage + stats sections regenerated.");
			}

			return;

			static string Pct(int have, int total)
			{
				return total == 0
					? "n/a"
					: $"{100.0 * have / total:0.0}%";
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
						? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
						: null;
					if (body == null || !PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					{
						Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} | (unreadable)");
						continue;
					}

					var slots = read
						.Message.InventoryEntries.Select(entry => NpcSpawnEquipmentBuilder.TryLift<NpcSpawnEquipment>(entry, out var row)
							// The row stores only slot/template/level now (the quad + hash are the Items row Template FKs to);
							// read the identity for the report off the captured entry itself.
							? $"slot{row.Pos}=tpl{row.Template}[k={entry.Item.Key1.Key1},{entry.Item.Key1.Key2},{entry.Item.Key1.Key3},{entry.Item.Key1.Key4} lvl={row.Level} hash={entry.Item.Hash}]"
							: "slot?=unliftable")
						.ToList();
					Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} pos=({e.X:0.#},{e.Z:0.#})");
					foreach (var s in slots)
						Console.WriteLine($"    {s}");
				}
		}

		private static void ItemTemplate(string rdbDir, string? arg, string? outDir = null)
		{
			if (!uint.TryParse(arg, out var itemId))
			{
				Console.WriteLine($"item-template needs a numeric item id in --report-arg, got '{arg}'");
				return;
			}

			using var db = OpenDb();
			var item = db.Items.FirstOrDefault(i => i.Id == itemId);
			if (item == null)
			{
				Console.WriteLine($"item {itemId} not in DB");
				return;
			}

			Console.WriteLine($"item {itemId} \"{item.Name}\" type={item.Type} cat={item.SearchCategory} key={item.Key.Key1:X8},{item.Key.Key2:X8},{item.Key.Key3:X8},{item.Key.Key4:X8}");
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
			byte[]? template = null;
			foreach (var type in new uint[] { 1000020, 1000620 })
			{
				if (rdb.TryGet(type, itemId, out var e) && e.Size > 0 && rdb.CanRead(e))
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
					var fid = body.ReadU32Le(j * 4);
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
					var fid = body.ReadU32Le(j * 4);
					if (fid == 0 || fid > 2000 || (have && fid >= last))
						break;

					var val = body.ReadU32Le((j + 1) * 4);
					var resolves = "";
					foreach (var t in new uint[] { 1010030, 1010201, 1010001, 1010200, 1000700, 1010700 })
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
			var refTypes = new uint[] { 1010030, 1010201, 1010001, 1010200 };
			for (var i = 0; i + 4 <= body.Length; i += 4)
			{
				var v = body.ReadU32Le(i);
				if (v < 1000)
					continue;

				foreach (var t in refTypes)
				{
					if (rdb.TryGet(t, v, out var re) && re.Size > 0)
						Console.WriteLine($"  @{i,5} {v} -> type {t} ({(re.File == 0xff ? "CDN" : "local")} {re.Size} B)");
				}
			}
		}

		// Every LDB page (RDB 1030002) in the client, as (page id, category, id -> string). Thin wrapper over the shared
		// LdbStringIndex grammar that adds only the per-PAGE grouping the crib reports need - language is a property of
		// the page, so a report that merges every page of a category ends up with whichever language sorted last.
		private static IEnumerable<(uint PageId, uint Category, Dictionary<uint, string> Strings)> LdbPagesOf(RdbReader rdb)
		{
			foreach (var e in rdb.EntriesOfType(1030002).Where(x => x.IsLocal))
			{
				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // not a readable record (CDN-only / not zlib)
				}

				var category = LdbStringIndex.CategoryOf(body);
				if (category == 0)
					continue; // not a TDC2 page

				var strings = new Dictionary<uint, string>();
				foreach (var entry in LdbStringIndex.Parse(body))
					strings[entry.Id] = entry.Value;

				yield return (e.Id, category, strings);
			}
		}

		// The client's own STAT-NAME table: LDB category 615 is an id -> name map keyed by the WIRE STAT ID (the same
		// id space as the 0x96B8DC59 vitals block, the FullChar snapshot stat sheet, the item [2][9] modifier region and
		// the spell stat effects).
		//
		// Like every LDB category it is LOCALIZED - one page per language, all carrying the same ids - so the page must
		// be CRIBBED, not picked by order (taking the last page yields Korean). The crib is the same one the spell/item
		// name reports use (#291/#300), against the ids we had already named the hard way: the English page is the one
		// whose text matches CharacterStatId's own names. That cross-check is also what makes this a linter and not just
		// a dump - a name we invented that disagrees with the client's is a bug, and an enum id absent from the table is
		// one we named from somewhere else (Ghidra, tooltips) and should stay justified in its comment.
		private static void LdbStatNames(string rdbDir, string? outDir)
		{
			using var rdb = new RdbReader(rdbDir);

			// Our own names, normalized to letters+digits, as the crib key set.
			static string Key(string s)
			{
				return Regex.Replace(s, "[^A-Za-z0-9]", "").ToLowerInvariant();
			}

			var ourNames = Enum.GetValues<CharacterStatId>().Distinct().ToDictionary(id => (uint)id, id => Key(Enum.GetName(id)!));

			var scored = LdbPagesOf(rdb)
				.Where(p => p.Category == LdbStringIndex.StatNameCategory)
				.Select(p => (p.PageId, p.Strings, Match: p.Strings.Count(kv => ourNames.TryGetValue(kv.Key, out var ours) && Key(kv.Value) == ours)))
				.OrderByDescending(x => x.Match)
				.ToList();

			if (scored.Count == 0)
			{
				Console.WriteLine($"LDB cat {LdbStringIndex.StatNameCategory}: no pages found - is this RDB dir complete?");
				return;
			}

			Console.WriteLine($"LDB cat {LdbStringIndex.StatNameCategory}: {scored.Count} page(s); cribbed against {ourNames.Count} named CharacterStatId members:");
			foreach (var (pageId, strings, match) in scored)
				Console.WriteLine($"  page {pageId,-9} ids={strings.Count,-5} crib-match={match,-4} {(pageId == scored[0].PageId ? "<-- ENGLISH" : "")}");

			var names = scored[0].Strings;

			var lines = names.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}\t0x{kv.Key:X4}\t{kv.Value}").ToList();
			foreach (var line in lines)
				Console.WriteLine("  " + line);

			// Cross-check: every CharacterStatId member against the client's name for that id.
			int agree = 0, absent = 0;
			var disagree = new List<string>();
			foreach (var id in Enum.GetValues<CharacterStatId>().Distinct())
			{
				var member = Enum.GetName(id)!;
				if (!names.TryGetValue((uint)id, out var ldb))
				{
					absent++;
					continue;
				}

				// Compare on letters+digits only: our names are PascalCase, the client's are spaced/parenthesised.
				var ours = Regex.Replace(member, "[^A-Za-z0-9]", "").ToLowerInvariant();
				var theirs = Regex.Replace(ldb, "[^A-Za-z0-9]", "").ToLowerInvariant();
				if (ours == theirs)
					agree++;
				else
					disagree.Add($"  0x{(uint)id:X4} {member} != '{ldb}'");
			}

			Console.WriteLine($"CharacterStatId: {agree} names match the client's, {disagree.Count} differ, {absent} have no entry in the table.");
			foreach (var d in disagree)
				Console.WriteLine(d);

			if (string.IsNullOrEmpty(outDir))
				return;

			Directory.CreateDirectory(outDir);
			var path = Path.Combine(outDir, "ldb-stat-names.tsv");
			File.WriteAllLines(path, lines);
			Console.WriteLine($"wrote {path}");
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
					if (p.Opcode != 0x2000u || p.SubOpcode != (uint)GameServer0X2000Opcodes.PlayfieldFIIR || p.Bytes.Length < 16)
						continue;

					var body = p.Bytes[(12 + (int)p.Bytes.ReadU32Be(8))..];
					if (body.Length < 79)
						continue;

					var map = body.ReadU32Be(34);
					var masked = (byte[])body.Clone();
					Array.Clear(masked, 42, 4); // instance
					Array.Clear(masked, 58, 4); // playfield token
					Array.Clear(masked, 75, 4); // playfield token (2nd)
					(byMap.TryGetValue(map, out var l)
						? l
						: byMap[map] = []).Add((c.Name, Convert.ToHexString(masked)));
				}

			foreach (var (map, list) in byMap.OrderBy(kv => kv.Key))
			{
				var distinct = list.Select(x => x.Masked).Distinct().ToList();
				Console.WriteLine($"map=0x{map:X8}  {list.Count} caps  {distinct.Count} distinct masked  {(distinct.Count == 1 ? "CONSISTENT" : "*** VARIES ***")}");
				if (distinct.Count > 1)
				{
					foreach (var d in distinct)
						Console.WriteLine($"    {d}  <- {string.Join(", ", list.Where(x => x.Masked == d).Select(x => x.Cap[..Math.Min(34, x.Cap.Length)]).Distinct().Take(4))}");
				}
			}
		}

		// Matrix CSV, one row per capture, cell = packet count. Columns are the (direction, opcode) summary first
		// (sub-opcode collapsed, ~122), then EVERY (direction, opcode, sub-opcode) appended at the end (header
		// "S2C 0x2000:0x737A6DF9"). Wide (~10k sub columns, many are one-off entity handles) but a full pivot table.
		private static void OpcodeCsv(IReadOnlyList<NamedCapture> captures)
		{
			var perCapOp = new Dictionary<string, Dictionary<(bool Dl, uint Op), int>>();
			var perCapSub = new Dictionary<string, Dictionary<(bool Dl, uint Op, uint Sub), int>>();
			var opCols = new SortedSet<(uint Op, bool Dl)>();
			var subCols = new SortedSet<(uint Op, uint Sub, bool Dl)>();
			foreach (var c in captures)
			{
				var ops = new Dictionary<(bool Dl, uint Op), int>();
				var subs = new Dictionary<(bool Dl, uint Op, uint Sub), int>();
				foreach (var p in c.Packets)
				{
					ops[(p.IsDownload, p.Opcode)] = ops.GetValueOrDefault((p.IsDownload, p.Opcode)) + 1;
					subs[(p.IsDownload, p.Opcode, p.SubOpcode)] = subs.GetValueOrDefault((p.IsDownload, p.Opcode, p.SubOpcode)) + 1;
					opCols.Add((p.Opcode, p.IsDownload));
					subCols.Add((p.Opcode, p.SubOpcode, p.IsDownload));
				}

				perCapOp[c.Name] = ops;
				perCapSub[c.Name] = subs;
			}

			var ops2 = opCols.ToList();
			var subs2 = subCols.ToList();

			Console.Error.WriteLine($"{ops2.Count} opcode columns + {subs2.Count} sub-opcode columns across {captures.Count} captures");
			Console.WriteLine("capture," + string.Join(",", ops2.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Op:X4}")) + "," +
							  string.Join(",", subs2.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Op:X4}:0x{x.Sub:X8}")));
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var op = perCapOp[c.Name];
				var sub = perCapSub[c.Name];
				Console.WriteLine(Csv(c.Name) + "," + string.Join(",", ops2.Select(x => op.TryGetValue((x.Dl, x.Op), out var n)
					? n
					: 0)) + "," + string.Join(",", subs2.Select(x => sub.TryGetValue((x.Dl, x.Op, x.Sub), out var n)
					? n
					: 0)));
			}

			return;

			static string Csv(string s)
			{
				return s.Contains(',') || s.Contains('"')
					? $"\"{s.Replace("\"", "\"\"")}\""
					: s;
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
					(globalDumps.TryGetValue(k, out var s)
						? s
						: globalDumps[k] = []).Add(c.Name);
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
				.OrderBy(x => x.Key.Op)
				.ThenBy(x => x.Key.Dl)
				.ToList();
			Console.WriteLine($"## Opcodes (sub-opcode collapsed) - {byOp.Count} distinct (dir,op)");
			foreach (var x in byOp)
				Console.WriteLine($"{(x.Key.Dl ? "S2C" : "C2S")} op=0x{x.Key.Op:X4}  total={x.Total}  {x.Subs} subs  in {x.Dumps} dumps");
			Console.WriteLine();

			Console.WriteLine($"## Global (sub)opcode catalog - {totalPackets} packets, {globalCount.Count} distinct (dir,op,sub) tuples");
			foreach (var (k, count) in globalCount.OrderBy(kv => kv.Key.Op).ThenBy(kv => kv.Key.Sub).ThenBy(kv => kv.Key.Dl))
				Console.WriteLine($"{(k.Dl ? "S2C" : "C2S")} op=0x{k.Op:X4} sub=0x{k.Sub:X8}  total={count}  in {globalDumps[k].Count} dumps");
		}

		// Reports read the DB from the shared config (faolan.db by default, --Database:ConnectionString to override); one short-lived context.
		private static DatabaseContext OpenDb()
		{
			_db ??= CliConfig.ResolveDatabase(_options.ConfigOverrides);
			return DatabaseExtensions.CreateContext(_db.Value.Provider, _db.Value.Connection);
		}

		// RDB -> 3D-model asset graph (Gitea #275, Phase 0). With no --report-arg: a corpus summary (how many
		// MESHINDEXFILE manifests resolve to geometry, how their textures are split local vs CDN). With --report-arg
		// <meshIndexId>: print that model's graph (RenderMesh id, materials, texture refs) and, if --out <dir> is given,
		// extract its FCTX textures to DDS (+ --dump-mesh to also write the raw 1010001 geometry blob for the Phase 1 spike).
		private static void RdbAssets(string rdbDir, string? arg, string? outDir, bool dumpMesh)
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

		// The client's RDB folder is part of the game install, not this repo, so it comes from config like every
		// other setting - ONE name across all three input methods (#412). RDB-backed reports fail with a clear
		// message if it is not set anywhere.
		private static string RdbDir()
		{
			return CliConfig.Resolve(_options.ConfigOverrides)["RdbDir"] ??
				throw new InvalidOperationException(
					"The client's RDB folder is not configured - set RdbDir in faolan.json, the RdbDir environment variable, or pass --RdbDir <dir>.");
		}

		// List all entries of a TYPE (--report-arg <type>) and dump each decompressed payload to --out as
		// <type>_<id>.bin for offline inspection. Used to crack new RDB record formats (e.g. CHARACTERCREATOR 1010700).
		private static void RdbDump(string rdbDir, string arg, string? outDir)
		{
			// `<type>` dumps every entry of that type; `<type>:<id>` dumps just one - which is the only usable form for
			// a huge type like 1000020 (98k item templates).
			var colon = arg.IndexOf(':');
			uint? onlyId = null;
			if (colon >= 0)
			{
				if (!uint.TryParse(arg[(colon + 1)..], out var parsedId))
				{
					Console.WriteLine($"rdb-dump: '{arg[(colon + 1)..]}' is not a numeric id (expected <type> or <type>:<id>)");
					return;
				}

				onlyId = parsedId;
				arg = arg[..colon];
			}

			if (!uint.TryParse(arg, out var type))
			{
				Console.WriteLine($"rdb-dump needs a numeric type in --report-arg, got '{arg}' (expected <type> or <type>:<id>)");
				return;
			}

			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			if (outDir != null)
				Directory.CreateDirectory(outDir);

			foreach (var e in rdb.EntriesOfType(type).Where(rdb.CanRead))
			{
				if (onlyId != null && e.Id != onlyId)
					continue;

				var body = rdb.ReadDecompressed(e);
				if (body.Length == 0) // CDN miss
					continue;

				Console.WriteLine($"type={type} id={e.Id} decompressed={body.Length} B");
				if (outDir != null)
					File.WriteAllBytes(Path.Combine(outDir, $"{type}_{e.Id}.bin"), body);
			}
		}

		// List every (type, id) entry in the RDB index that has the given id, with availability. Used to discover what
		// id-space a cross-reference lives in (e.g. a character's HeadMesh / *hairmesh resource id) (#275).
		// The feat/ability catalog's RDB type, and the magic a PLAINTEXT record opens with (`TB\x0f\x00`).
		private const uint FeatPlaintextMagic = 0x000F4254;
		private const uint FeatTemplateType = 1000020;

		// Classify the RDB feat/ability catalog (type 1000020) into PLAINTEXT vs TEA-ENCRYPTED records, and dump the
		// plaintext ones (Gitea #251). A plaintext record opens with the `TB\x0f\x00` magic (u32 LE 0x000f4254) - the
		// same decisive marker the key-crack used as its validation - while an encrypted one opens with `RDBH`.
		// The plaintext share needs NO key, so it is a feat catalog we can build today.
		// --report-arg <substring>  filters the dumped names   --out <dir>  writes feat-catalog.tsv
		private static void FeatCatalog(string rdbDir, string? filter, string? outDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var plain = new List<(uint Id, string Name, byte[] Body)>();
			var encrypted = 0;
			var unreadable = 0;
			var other = 0;

			foreach (var e in rdb.EntriesOfType(FeatTemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					unreadable++;
					continue;
				}

				if (body.Length < 8)
				{
					other++;
					continue;
				}

				if (BinaryPrimitives.ReadUInt32LittleEndian(body) != FeatPlaintextMagic)
				{
					// `RDBH` is the TEA-encrypted template header; anything else is a shape we have not seen.
					if (body[0] == (byte)'R' && body[1] == (byte)'D' && body[2] == (byte)'B' && body[3] == (byte)'H')
						encrypted++;
					else
						other++;

					continue;
				}

				plain.Add((e.Id, RdbFeatImporter.ReadNameAndDescription(body).Name, body));
			}

			var total = plain.Count + encrypted + other + unreadable;
			Console.WriteLine($"RDB type {FeatTemplateType}: {total} records");
			Console.WriteLine($"  plaintext (TB magic) : {plain.Count} ({(total == 0 ? 0 : 100.0 * plain.Count / total):F1}%)");
			Console.WriteLine($"  TEA-encrypted (RDBH) : {encrypted}");
			Console.WriteLine($"  other / unreadable   : {other} / {unreadable}");

			var named = plain.Where(p => p.Name.Length > 0).ToList();
			Console.WriteLine($"  plaintext WITH a name: {named.Count}");

			// --report-arg <id>[,<id>...]: dump those records' FULL word arrays and then the offsets where they
			// DIFFER. Handed a rank series (same feat, consecutive ranks) the diff isolates the rank/tier/prereq
			// fields directly, which is how #251 step 2 cross-correlates same-tree feats.
			if (!string.IsNullOrWhiteSpace(filter) && filter.Split(',').All(x => uint.TryParse(x.Trim(), out _)))
			{
				var picked = new List<(uint Id, string Name, byte[] Body)>();
				foreach (var want in filter.Split(',').Select(x => uint.Parse(x.Trim())))
				{
					var rec = plain.FirstOrDefault(x => x.Id == want);
					if (rec.Body == null)
					{
						Console.WriteLine($"    id={want}: not a plaintext 1000020 record");
						continue;
					}

					picked.Add(rec);
					var words = new List<string>();
					for (var o = 0; o + 4 <= rec.Body.Length; o += 4)
						words.Add($"[{o,3}]={BinaryPrimitives.ReadUInt32LittleEndian(rec.Body.AsSpan(o))}");

					Console.WriteLine($"    id={rec.Id} len={rec.Body.Length} name='{rec.Name}'");
					Console.WriteLine($"      {string.Join(" ", words)}");
					Console.WriteLine($"      hex={Convert.ToHexString(rec.Body)}");
				}

				if (picked.Count > 1)
				{
					// Two alignments, because the NAME sits in the middle and its length varies: absolute offsets are
					// comparable only for equal-length names, while everything after the name lines up when measured
					// from the tail's start. Cross-FEAT comparison needs the tail view; a rank series can use either.
					Console.WriteLine("  HEADER diffs (absolute offset, valid while the names are the same length):");
					var headWords = picked.Min(p => TailStart(p.Body)) / 4;
					for (var wi = 0; wi < headWords; wi++)
					{
						var vals = picked.Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.Body.AsSpan(wi * 4))).ToList();
						if (vals.Distinct().Count() > 1)
							Console.WriteLine($"    [{wi * 4,3}] {string.Join(" -> ", vals)}");
					}

					Console.WriteLine("  TAIL words (offset RELATIVE to the end of the name; '=' means identical across the set):");
					var tailWords = picked.Min(p => (p.Body.Length - TailStart(p.Body)) / 4);
					for (var wi = 0; wi < tailWords; wi++)
					{
						var vals = picked.Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.Body.AsSpan(TailStart(p.Body) + (wi * 4)))).ToList();
						var same = vals.Distinct().Count() == 1;
						Console.WriteLine($"    +{wi * 4,-3} {(same ? "=" : " ")} {string.Join(" -> ", vals)}");
					}
				}

				return;
			}

			// Type 1000020 carries ITEMS and FEATS in one type, so the catalog is only useful with the discriminating
			// words alongside each name. Two look decisive on inspection - [28] (a category id: 7063 on feats,
			// 9081/10090 on the item templates checked) and [44] (0x10000005 vs 512) - but the rule is NOT hardcoded
			// here: the words go out as columns and the distribution is printed, so the split is chosen from the whole
			// catalog rather than from a handful of samples.
			var byFlags = named.GroupBy(p => Word(p.Body, 44)).OrderByDescending(g => g.Count()).ToList();
			Console.WriteLine("  [44] distribution (named records):");
			foreach (var g in byFlags.Take(8))
				Console.WriteLine($"    0x{g.Key:X8} x{g.Count(),-6} e.g. {g.First().Name}");

			var shown = named.Where(p => string.IsNullOrWhiteSpace(filter) || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
			foreach (var p in shown)
				Console.WriteLine($"    {p.Id,-9} cat={Word(p.Body, 28),-6} flags=0x{Word(p.Body, 44):X8} {p.Name}");

			if (string.IsNullOrWhiteSpace(outDir))
				return;

			Directory.CreateDirectory(outDir);
			var path = Path.Combine(outDir, "feat-catalog.tsv");
			using var w = new StreamWriter(path);
			w.WriteLine("id\tlength\tcategory\tflags\tname");
			foreach (var p in named.OrderBy(p => p.Id))
				w.WriteLine($"{p.Id}\t{p.Body.Length}\t{Word(p.Body, 28)}\t0x{Word(p.Body, 44):X8}\t{p.Name}");

			Console.WriteLine($"WROTE {path} ({named.Count} rows)");
		}

		// Where the record's TAIL begins: just past the name. Everything before it is the fixed header, everything
		// after is the variable part, and only the tail lines up between records whose names differ in length.
		private static int TailStart(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = Word(body, i);
				if (len is < 2 or > 96 || i + 4 + len > body.Length)
					continue;

				var printable = true;
				foreach (var b in body.AsSpan(i + 4, (int)len))
				{
					if (b is < 0x20 or > 0x7E)
					{
						printable = false;
						break;
					}
				}

				if (printable)
					return i + 4 + (int)len;
			}

			return body.Length;
		}

		// One little-endian u32 of a record, or 0 past its end.
		private static uint Word(byte[] body, int offset)
		{
			return offset + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) : 0;
		}


		// The record's NAME: a u32-length-prefixed ASCII run. The fixed header is [magic][id][1000000][...u32 fields]
		// and the name closes it - for the 96-byte form the length sits at offset 88 and the string ends the record
		// exactly. Scanned u32-aligned rather than pinned to 88, because the header is longer on the other record
		// shape (the field at offset 16 is 2 for the short form and 3 for the long one). Returns "" when nothing
		// there parses as a name, so a wrong guess shows up as an empty column rather than as mojibake.
		private static string FeatName(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i));
				if (len is < 2 or > 96 || i + 4 + len > body.Length)
					continue;

				var span = body.AsSpan(i + 4, (int)len);
				var printable = true;
				foreach (var b in span)
				{
					if (b is < 0x20 or > 0x7E)
					{
						printable = false;
						break;
					}
				}

				if (printable)
					return Encoding.ASCII.GetString(span);
			}

			return "";
		}

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
		// --report-arg <meshIndexId>  --out <dir>  [--skeleton <id>, default auto-select by bone palette].
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
		// The client's BALANCE-TWEAK table: RDB type 1000516, a single entry holding 294 named `double` constants
		// ("tweakables"). This is the game's own tuning sheet, and it is the authority for a pile of numbers we have so far
		// been guessing at - the out-of-combat regen attribute modifiers (TWK_OOCHealthRegenConModifier = 0.15,
		// TWK_OOCManaRegenIntModifier = 0.38, ...), the armour/protection targets per level
		// (TWK_TargetPerLevelArmor = 137.225), combat behaviour (TWK_GlancingHitBaseChance = 5,
		// TWK_InterruptChance = 30, TWK_OffhandDamagePercent = 0.5) and a set of spell/buff ids the server is expected
		// to cast itself (TWK_NanoExhaustedEffect, TWK_StaggeringBuffID, TWK_GM_Res_SpellID, ...).
		//
		// Grammar: [u32 type][u32 0][u32 1][u32 count] then count × ([u32 nameLen][nameLen ASCII][f64 value LE]).
		// It is NOT self-referential validation: TWK_NanoExhaustedEffect = 2960 is the exact spell id we independently
		// decoded as the energy-depletion "Exhausted" debuff, and TWK_Selection_Radius = 10 matches the interaction range.
		//
		// `--report-arg <substring>` filters by name (case-insensitive), e.g. `--report-arg regen`.
		private static void RdbTweaks(string rdbDir, string? filter)
		{
			const uint tweakType = 1000516;
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			var entries = rdb.EntriesOfType(tweakType).ToList();
			if (entries.Count == 0)
			{
				Console.WriteLine($"no RDB entries of type {tweakType} (the tweak table) - is this a full client install?");
				return;
			}

			foreach (var entry in entries)
			{
				var blob = rdb.ReadDecompressed(entry);
				if (blob.Length < 16)
				{
					Console.WriteLine($"tweak entry {entry.Id}: {blob.Length} B - too short");
					continue;
				}

				var count = blob.ReadU32Le(12);
				Console.WriteLine($"tweak table type={tweakType} id={entry.Id}: {blob.Length} B, declares {count} tweak(s)");
				var off = 16;
				var read = 0;
				var shown = 0;
				while (off + 4 <= blob.Length && read < count)
				{
					var len = (int)blob.ReadU32Le(off);
					if (len <= 0 || off + 4 + len + 8 > blob.Length)
						break;

					var name = Encoding.ASCII.GetString(blob, off + 4, len);
					var value = BitConverter.ToDouble(blob, off + 4 + len);
					off += 4 + len + 8;
					read++;
					if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
						continue;

					Console.WriteLine($"  {name,-52} {value.ToString("0.######", CultureInfo.InvariantCulture)}");
					shown++;
				}

				Console.WriteLine(read == count
					? $"  ({read} parsed{(filter != null ? $", {shown} matching '{filter}'" : "")})"
					: $"  ⚠ parsed only {read} of the declared {count} - the grammar breaks at byte {off}");
			}
		}

		// `CHARACTER_HP_MANA_STAMINA` (RDB type 1000511) - the client's per-class BASE vitals curve, decoded from its own
		// parser (`HitsManaAndStamina_c::ReadFromRDB`, whose assert states the record size: 14 x int32).
		//
		//   [12-byte header][i32 version][i32 count][count x 14 x i32]
		//
		// Each record is keyed [race][class]; fields 2..4 are the L1 BASE health/mana/stamina (/100) and 5..7 their
		// per-level growth (/1000), so `base/100 + (level-1) * growth/1000` reproduces the captured snapshot sheet's stat
		// 1 / 507 / 505 exactly for every captured character. See docs/rdb/client-tweak-table.md. Gitea #3.
		// #444: the class attribute BASE. It lives in RDB type 1000020 - the ITEM-template type - as PLAINTEXT templates
		// (no TEA key), one per class per level, each NAMING itself "<Class> Class Feat <NNN>". Their stat records are
		// read by the ordinary item parser, so this only became readable once that scan went byte-granular (#441).
		//
		// ⚠️ What this report does NOT establish is the id -> LEVEL mapping. `templateId = base + (level - 1)` is
		// verified against all four client-observed anchors (Assassin L23 = 3074028+22, Herald of Xotli L24 =
		// 3130632+23, Necromancer L80 = 1173784+79, Demonologist L80 = 1126680+79), but the per-class BASE cannot be
		// recovered structurally: entries sharing the class name from other feat series are interleaved inside the id
		// range, so neither "lowest id" nor "longest monotonic contiguous window" finds it (the latter truncates the
		// Assassin's real 80 to 70). The name's <NNN> is not the level either - three consecutive Assassin templates
		// are all "Class Feat 021" - and no property field carries the level (field 33 is a per-CLASS code: Assassin
		// 23, Herald of Xotli 30, Necromancer 26, Demonologist 27).
		//
		// So a class's base needs ONE client observation to anchor it: log a character of that class in with the
		// ClientInterface + GameCode/ItemData channels on and read the template id it loads. This report gives the
		// candidate set and their values; the anchor gives the offset.
		private static void ClassAttributeRuns(string rdbDir, string? outDir)
		{
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			var csv = new StringBuilder("class,block,level,templateId,statId,amount\n");
			var byClass = ClassAttributeBaseModule.ScanRuns(rdb);

			foreach (var (cls, candidates) in byClass.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
			{
				var byLevel = ClassAttributeBaseModule.LevelsOf(candidates);
				var checks = ClassAttributeBaseModule.ClientObservations.Where(o => o.Class == cls)
					.Select(o => byLevel.GetValueOrDefault(o.Level).Id == o.TemplateId
						? $"L{o.Level}={o.TemplateId} ✓"
						: $"⚠ L{o.Level} resolved to {byLevel.GetValueOrDefault(o.Level).Id}, client observed {o.TemplateId}");
				Console.WriteLine($"{cls,-16} {byLevel.Count,3}/{ClassAttributeBaseModule.MaxLevel} levels from {candidates.Count,3} candidates   " +
								  string.Join("  ", checks));

				// Every candidate, by the block its NAME declares - so a block carrying more than its ten levels (a
				// stray from another feat series sharing the class name) is visible rather than silently truncated.
				foreach (var block in candidates.GroupBy(t => t.BlockFirstLevel).OrderBy(g => g.Key))
				{
					var ordered = block.OrderBy(t => t.AttributeSum).ThenBy(t => t.Id).ToList();
					Console.WriteLine($"    L{block.Key,2}-{block.Key + 9,2}  {ordered.Count,2} candidates  " +
									  $"{string.Join(" ", ordered.Select(t => t.Id))}");
					if (ordered.Count != 10)
						foreach (var t in ordered)
							Console.WriteLine($"        {t.Id}  [{Fmt(t.Mods)}]");
				}

				foreach (var t in candidates.OrderBy(x => x.BlockFirstLevel).ThenBy(x => x.AttributeSum))
					foreach (var mod in t.Mods)
						csv.Append($"{cls},{t.BlockFirstLevel},{byLevel.FirstOrDefault(kv => kv.Value.Id == t.Id).Key},{t.Id},{mod.StatId},{mod.WireAmount}\n");
			}

			if (outDir == null)
				return;

			Directory.CreateDirectory(outDir);
			var path = Path.Combine(outDir, "class-attribute-runs.csv");
			File.WriteAllText(path, csv.ToString());
			Console.WriteLine($"\nwrote {path}");
		}

		private static readonly CharacterStatId[] Attributes =
			[CharacterStatId.Strength, CharacterStatId.Constitution, CharacterStatId.Intelligence, CharacterStatId.Wisdom, CharacterStatId.Dexterity];

		private static string Fmt(List<ItemTemplateStatModifiers.Modifier> mods)
		{
			return string.Join(" ", mods.Select(x => $"{(CharacterStatId)x.StatId}={x.WireAmount}"));
		}

		private static void RdbClassVitals(string rdbDir, string? filter)
		{
			const uint tableType = 1000511;
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			foreach (var entry in rdb.EntriesOfType(tableType))
			{
				var blob = rdb.ReadDecompressed(entry);
				if (blob.Length < 20)
				{
					Console.WriteLine($"entry {entry.Id}: {blob.Length} B - too short");
					continue;
				}

				var version = blob.ReadI32Le(12);
				var count = blob.ReadI32Le(16);
				var expected = 20 + count * 56;
				Console.WriteLine($"type={tableType} id={entry.Id}: {blob.Length} B, version={version}, {count} record(s)" +
								  (blob.Length == expected
									  ? " (size matches 20 + count x 56 exactly)"
									  : $" ⚠ expected {expected} B"));
				Console.WriteLine($"  {"race",4} {"class",5} {"hpBase",7} {"hpGrow",7} {"manaBase",9} {"manaGrow",9} {"stamBase",9} {"stamGrow",9}   trailing");
				for (var i = 0; i < count && 20 + (i + 1) * 56 <= blob.Length; i++)
				{
					var r = new int[14];
					for (var k = 0; k < 14; k++)
						r[k] = blob.ReadI32Le(20 + i * 56 + k * 4);

					var cls = ((ConanClass)r[1]).ToString();
					if (filter != null && !cls.Contains(filter, StringComparison.OrdinalIgnoreCase) && !r[1].ToString().Contains(filter))
						continue;

					Console.WriteLine($"  {r[0],4} {cls,5} {r[2] / 100.0,7:0.##} {r[5] / 1000.0,7:0.###} {r[3] / 100.0,9:0.##} {r[6] / 1000.0,9:0.###} " +
									  $"{r[4] / 100.0,9:0.##} {r[7] / 1000.0,9:0.###}   {string.Join(",", r[8..])}");
				}
			}
		}

		// Gitea #441: every keyed item's TEMPLATE-SCALING inputs next to its EXPLICIT [2][9] stat records, as CSV, so the
		// scaling formula can be FITTED on the items that carry both and then applied to the ones that carry none.
		//
		// Why this exists: only ~24% of items carry explicit stat records. The rest are "template-scaled" - a high-level
		// piece like Talons of Dark Terror has a rich in-client tooltip but ZERO records, so its armor/health never
		// reaches the server and a geared character's bars cap far below what the client draws (#3). The scaling inputs
		// are the property-table fields every one of them DOES carry: 54 = item level, 499 = quality tier, 496 = the
		// per-item budget that tracks quality (3250/3500 at tier 3, 4000/5000 at tier 4), 360, 41, 21, plus the search
		// CATEGORY (30000 armor / 30020 neck / 30023 cloak) which stands in for the slot.
		//
		// Writing BOTH sides in one row is the point: an item with an explicit Armor/HealthMax record is ground truth for
		// what the same inputs must produce, and there is no need to ask the client for a tooltip to fit against.
		private static void ItemStatScaling(string rdbDir, string? outPath)
		{
			using var db = OpenDb();
			var items = db.Items.Include(i => i.StatModifiers)
				.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0)
				.OrderBy(i => i.Id)
				.ToList();

			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			uint[] fields = [54, 499, 496, 360, 41, 21, 644, 645, 74];
			CharacterStatId[] stats = [CharacterStatId.Armor, CharacterStatId.HealthMax, CharacterStatId.ManaMax, CharacterStatId.StaminaMax, CharacterStatId.Constitution];

			var rows = new List<string>
			{
				"id,name,category,quality,regionRecords," + string.Join(",", fields.Select(f => $"f{f}")) + "," + string.Join(",", stats.Select(s => $"explicit_{s}"))
			};
			int noEntry = 0, failed = 0, scanned = 0, withRegion = 0;
			foreach (var item in items)
			{
				var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
				byte[]? template = null;
				foreach (var type in new uint[] { 1000020, 1000620 })
				{
					if (!rdb.TryGet(type, item.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
						continue;
					try
					{
						template = rdb.ReadDecrypted(e, key);
						break;
					}
					catch
					{
						// a wrong/stale key - counted below, not fatal to the sweep
					}
				}

				if (template == null)
				{
					noEntry++;
					continue;
				}

				if (template.Length < 16)
				{
					failed++;
					continue;
				}

				scanned++;
				var body = template.AsSpan(12);
				var region = ItemTemplateStatModifiers.Parse(body);
				if (region.Count > 0)
					withRegion++;

				var cells = new List<string>
				{
					item.Id.ToString(),
					"\"" + (item.Name ?? "").Replace("\"", "'") + "\"",
					item.SearchCategory.ToString(),
					item.Quality is { } q
						? ((uint)q).ToString()
						: "",
					region.Count.ToString()
				};
				foreach (var f in fields)
				{
					cells.Add(RdbItemImporter.TryReadPropertyField(body, f, out var v)
						? v.ToString()
						: "");
				}

				// The explicit side: the region record for each stat we care about, at RAW wire scale (the fit divides).
				cells.AddRange(stats.Select(s =>
				{
					var hit = region.FirstOrDefault(m => m.StatId == (uint)s);
					return hit.StatId == (uint)s
						? hit.WireAmount.ToString()
						: "";
				}));
				rows.Add(string.Join(",", cells));
			}

			var path = outPath ?? Path.Combine(Path.GetTempPath(), "item-stat-scaling.csv");
			File.WriteAllLines(path, rows);
			Console.WriteLine($"item-stat-scaling: {scanned} templates decrypted ({withRegion} with explicit records, {scanned - withRegion} template-scaled), {noEntry} without a readable/decryptable entry, {failed} too short.");
			Console.WriteLine($"wrote {path} ({rows.Count - 1} rows)");
		}

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
					var fid = body.ReadU32Le(j * 4);
					if (fid == 0 || fid > 2000 || (haveLast && fid <= last))
						break;

					cur[fid] = body.ReadU32Le((j + 1) * 4);
					last = fid;
					haveLast = true;
					j++;
				}

				if (cur.Count > best.Count)
					best = cur;
			}

			return best;
		}

		// The decompressed body of a per-map RDB resource, or empty when the map has no local entry of that type.
		private static byte[] ReadResource(RdbReader rdb, uint type, uint mapId)
		{
			if (!rdb.TryGet(type, mapId, out var e) || !e.IsLocal)
				return [];

			try
			{
				return rdb.ReadDecompressed(e);
			}
			catch
			{
				return [];
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
					if (!p.IsDownload || p.SubOpcode != (uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR)
						continue;

					var body = p.Bytes.Length >= 16
						? p.Bytes[(12 + (int)p.Bytes.ReadU32Be(8))..]
						: [];
					if (body.Length >= 16)
						snaps.Add((c.Name, body));
				}

			foreach (var (cap, body) in snaps.OrderByDescending(s => s.Body.Length).Take(6))
			{
				var positions = new List<int>();
				for (var i = 0; i + 4 <= body.Length; i++)
				{
					if (body[i] == 0x73 && body[i + 1] == 0x7A && body[i + 2] == 0x6D && body[i + 3] == 0xF9)
						positions.Add(i);
				}

				var instance = body.ReadU32Be(12);
				Console.WriteLine(
					$"{cap[..Math.Min(46, cap.Length)],-46} size={body.Length,8} instance=0x{instance:X8} 0x{(uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR:X8}@[{string.Join(",", positions)}]");
				Console.WriteLine($"    first48: {Convert.ToHexString(body.AsSpan(0, Math.Min(48, body.Length)))}");

				try
				{
					var s = new FaolanStream(body);
					s.ReadUInt32();
					s.ReadUInt32();
					s.ReadUInt32();
					s.ReadUInt32();
					var rootMsg = FullCharSnapshotCodec.ParseInventoryRoot(s);
					Console.WriteLine($"    inventory-root: OK ({rootMsg.Inventories.Count} containers)");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"    inventory-root: FAIL - {ex.GetType().Name}: {ex.Message}");
				}
			}
		}

		// TEMP (#274): discover the spell DURATION field in the RDB spell-data records (type 1040005). Dumps either a
		// specific spell's full increasing field table (--report-arg <decimal id>) or, with no arg, a histogram of how
		// often each field id appears across all named spell records (so a duration field stands out as near-universal
		// with ms-range values). Field 0x126=cast/0x11F=range/0x15C=damage are already known.
		private static void SpellFields(string rdbDir, string? arg)
		{
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			var counts = new Dictionary<uint, int>();
			var samples = new Dictionary<uint, List<uint>>();
			uint? wanted = uint.TryParse(arg, out var w)
				? w
				: null;
			foreach (var e in rdb.EntriesOfType(1040005))
			{
				if (e.Size == 0 || !rdb.CanRead(e))
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
				if (wanted is { } id && e.Id == id)
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
						samples[kv.Key] = l = [];
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

		// Matrix CSV for ONE opcode (arg, e.g. "0x2000"; default 0x2000): one row per capture, one column per
		// (direction, sub-opcode) seen under that opcode, cell = packet count. This is the drill-down of opcodes-csv -
		// e.g. every S2C 0x2000 IIR type as its own column. High-cardinality opcodes (0x2008/0x2020 handles) will be wide.
		private static void SubOpcodeCsv(IReadOnlyList<NamedCapture> captures, string? arg)
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

			Console.Error.WriteLine($"opcode 0x{op:X4}: {ordered.Count} distinct (dir,sub) columns across {captures.Count} captures");
			Console.WriteLine("capture," + string.Join(",", ordered.Select(x => $"{(x.Dl ? "S2C" : "C2S")} 0x{x.Sub:X8}")));
			foreach (var c in captures.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var counts = perCap[c.Name];
				Console.WriteLine(Csv(c.Name) + "," + string.Join(",", ordered.Select(x => counts.TryGetValue((x.Dl, x.Sub), out var n)
					? n
					: 0)));
			}

			return;

			static string Csv(string s)
			{
				return s.Contains(',') || s.Contains('"')
					? $"\"{s.Replace("\"", "\"\"")}\""
					: s;
			}
		}

		// #284: bake a compact per-map terrain file (heightmap 1066611 + collision mesh 1000012, gzipped) for every map
		// in the DB that has terrain, so the GAME SERVER can serve ground-snap + line-of-sight from these small files
		// instead of shipping the ~28 GB client RDB. Output: <out>/<mapId>.terrain (TerrainFile format). Run:
		//   dotnet run --project Faolan.Importer -- --report terrain-export [--rdb <dir>] [--terrain-out <dir>]
		private static async Task TerrainExport(string rdbDir, string outDir)
		{
			using var db = OpenDb();
			using var rdb = new RdbReader(rdbDir, ImportContext.DefaultProxyBase);
			Directory.CreateDirectory(outDir);

			var mapIds = await db.Maps.Select(m => m.Id).OrderBy(id => id).ToListAsync();
			int written = 0, skipped = 0;
			long totalBytes = 0;
			foreach (var mapId in mapIds)
			{
				var heightmap = ReadResource(rdb, RdbHeightmap.ResourceType, mapId);
				var collision = ReadResource(rdb, RdbCollisionMesh.ResourceType, mapId);
				if (heightmap.Length == 0 && collision.Length == 0)
				{
					skipped++;
					continue;
				}

				var extent = TilemapExtent(rdb, mapId);
				var path = Path.Combine(outDir, $"{mapId}{TerrainFile.Extension}");
				await using (var fs = File.Create(path))
					TerrainFile.Write(fs, extent, heightmap, collision);

				var size = new FileInfo(path).Length;
				totalBytes += size;
				written++;
				Console.WriteLine($"  map {mapId}: heightmap {heightmap.Length / 1024} KB + collision {collision.Length / 1024} KB -> {size / 1024} KB");
			}

			Console.WriteLine($"terrain-export: {written} maps written ({totalBytes / 1024 / 1024} MB total), {skipped} maps with no terrain, into {outDir}.");
		}

		// The playfield's world-unit width (1070003 <tilemap Width>), for the heightmap cell size. 0 if unavailable.
		private static uint TilemapExtent(RdbReader rdb, uint mapId)
		{
			if (!rdb.TryGet(1070003, mapId, out var e) || !e.IsLocal)
				return 0;

			try
			{
				var xml = Encoding.Latin1.GetString(rdb.ReadDecompressed(e));
				var m = Regex.Match(xml, "<tilemap[^>]*Width=\"(\\d+)\"");
				return m.Success
					? uint.Parse(m.Groups[1].Value)
					: 0;
			}
			catch
			{
				return 0;
			}
		}

		// `--report sql-seed [--report-arg <dir>]` (#411): the dialect-neutral SQL seed - the game DATA as one file
		// that loads into SQLite and Postgres alike, plus a small per-dialect sidecar for the auto-increment
		// counters. Writes to the repo root by default, which is where faolan.sql is committed.
		private static async Task<int> SqlSeed(string? outputDir)
		{
			var directory = outputDir ?? ".";
			await using var db = OpenDb();
			var body = Path.Combine(directory, "faolan.sql");
			var (tables, rows) = SqlSeedWriter.Write(db, body, Path.Combine(directory, "faolan.seq.sqlite.sql"), Path.Combine(directory, "faolan.seq.postgres.sql"));
			Console.WriteLine($"wrote {body} ({tables} tables, {rows} rows) + the sqlite/postgres counter sidecars");
			return 0;
		}

		// `--report apply-seed [--dry-run]` (#412): upgrade an EXISTING database's game content from faolan.sql.
		// The seed only loads itself into an EMPTY database, so this is how a database you have been playing on picks
		// up newer content - as a merge that leaves characters and everything else player-owned alone.
		private static int ApplySeed()
		{
			var dryRun = _options.DryRun;
			using var db = OpenDb();
			var report = SqlSeedApplier.Apply(db, Global.SeedPath, dryRun);

			foreach (var change in report.Changes.Where(c => c.Any).OrderByDescending(c => c.Inserted + c.Updated + c.Deleted))
				Console.WriteLine($"  {change.Table,-28} +{change.Inserted} ~{change.Updated} -{change.Deleted}");

			if (report.Changes.All(c => !c.Any))
				Console.WriteLine("  the database already matches the seed - nothing to do.");

			if (report.Blocked)
			{
				Console.Error.WriteLine();
				Console.Error.WriteLine("REFUSED: the new seed drops rows that are still referenced from outside it:");
				foreach (var blocker in report.Blockers)
					Console.Error.WriteLine($"  {blocker}");

				Console.Error.WriteLine("Nothing was written. Resolve those references (or keep the rows) and re-run - deleting");
				Console.Error.WriteLine("someone's character data to make a content upgrade fit is not a call this tool makes.");
				return 1;
			}

			Console.WriteLine(dryRun
				? $"DRY RUN - would apply +{report.Inserted} ~{report.Updated} -{report.Deleted} rows."
				: $"applied +{report.Inserted} ~{report.Updated} -{report.Deleted} rows.");
			return 0;
		}

		// Rewrites the content between the named markers (<!-- MARKER:start -->/<!-- MARKER:end -->) in README.md,
		// leaving the rest untouched.
		private static void UpdateReadmeSection(string marker, string block)
		{
			var path = "README.md"; // CWD-relative: the importer runs from the repo root
			var text = File.ReadAllText(path);
			var start = $"<!-- {marker}:start -->";
			var end = $"<!-- {marker}:end -->";
			var s = text.IndexOf(start, StringComparison.Ordinal);
			var e = text.IndexOf(end, StringComparison.Ordinal);
			if (s < 0 || e < 0 || e < s)
			{
				Console.WriteLine($"README.md has no {start}/{end} markers; skipping.");
				return;
			}

			File.WriteAllText(path, text[..(s + start.Length)] + "\n" + block + "\n" + text[e..]);
		}

		// The stand-in wire handle the WorldObj diagnostic builds with - see the note at its use.
		private const uint DiagnosticHandle = 0x00000001;

		// Build a named world object via the burstless world-entry path and print its hex (render diffing).
		private static async Task WorldObj(string target)
		{
			await using var db = OpenDb();
			foreach (var row in await db.WorldObjects.Where(o => o.Name!.Contains(target)).ToListAsync())
			{
				byte[]? baseBody = null;
				var baseOk = false;
				try
				{
					// Any handle will do: this prints a body for eyeballing/diffing the RENDER, and the handle is live
					// state the zone mints at spawn (see LiveWorldObject), so no row carries one to be faithful to.
					baseBody = WorldObjectBuilder.Build(row, DiagnosticHandle);
					baseOk = true;
				}
				catch (InvalidOperationException)
				{
					// Un-lifted row - this diagnostic REPORTS buildability per row, so catch the fail-fast throw and
					// record it as a failure instead of aborting the sweep.
				}

				var rewriteOk = baseOk && SpawnPlayfieldRewriter.TryRewrite(baseBody!, 0x01EA985Cu);
				Console.WriteLine($"map {row.MapId} {row.Name} baseBuild={baseOk} rewrite={rewriteOk} asset={row.GeometryAssetId}/{row.GeometryAssetType} words={row.Words.Count}");
				if (baseOk)
					Console.WriteLine("  BUILT: " + Convert.ToHexString(baseBody!).ToLowerInvariant());
			}
		}
	}
}
