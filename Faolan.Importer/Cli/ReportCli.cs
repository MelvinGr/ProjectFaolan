using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.GameServer.Combat;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Importer.Modules;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Importer.Cli
{
	// Read-only diagnostics (the old --dump-*/--burst-coverage flags), routed through `--report <name>`.
	// These inspect captures or the DB and print; they never write, so they bypass the import runner entirely.
	public static partial class ReportCli
	{
		private static (FaolanDbProvider Provider, string Connection)? _db;

		// DB config (provider + connection) for the DB-backed reports. Resolved LAZILY on the first OpenDb so the
		// capture/RDB-only reports (fixtures, coverage-of-captures, ...) need no DB config at all. Same source
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

			if (report == "terrain-los")
			{
				await TerrainLos(arg);
				return 0;
			}

			// DB-only, no corpus and no RDB: how much of the imported spell-effect table reaches a live engine (#701).
			if (report == "spell-effect-gap")
			{
				await SpellEffectGap(arg);
				return 0;
			}

			// RDB-only, no corpus: the client's stat-name table (LDB cat 615).
			if (report == "ldb-stat-names")
			{
				LdbStatNames(RdbDir(), options.Out);
				return 0;
			}

			// RDB + DB, no corpus: the ambient-bark catalog's readable set (#331).
			if (report == "npc-chatter")
			{
				await NpcChatter(RdbDir());
				return 0;
			}

			// RDB (+ DB for the cross-checks), no corpus: the bounded-area trigger-volume survey (#513).
			if (report == "bounded-areas")
			{
				await BoundedAreas(RdbDir(), arg, CapturedBanners(capturesDir));
				return 0;
			}

			// RDB (ciphertext) + DB (the captured per-string keys), no corpus: the encrypted quest text (#520).
			if (report == "ldb-quest-text")
			{
				await LdbQuestText(RdbDir(), arg);
				return 0;
			}

			// RDB + DB, no corpus: the client's own quest catalog, RDB type 1000104 (#569). The DEFINITION side of the
			// quest system - every quest in the game, with the designers' names in plaintext. The DB half is the
			// captured `JournalSequence`, which the step-order check scores the catalog's own row order against.
			if (report == "rdb-quest-catalog")
			{
				await RdbQuestCatalog(RdbDir(), arg);
				return 0;
			}

			// RDB (plaintext LDB) + DB (the stat cores), no corpus: the NPC PROFESSION catalog - what each
			// ProfessionName hash resolves to, grouped by the role tag beside it (#649).
			if (report == "professions")
			{
				await Professions(RdbDir());
				return 0;
			}

			// RDB (plaintext LDB) + DB (the goal catalog), no corpus: what each goal's TRAILING typed reference resolves
			// to, and whether that string can serve as the match key `TargetName` is (#521).
			if (report == "quest-goal-targets")
			{
				await QuestGoalTargets(RdbDir());
				return 0;
			}

			// The NPC-dialogue half of the same encrypted set (categories 50000-50003); --report-arg filters by NPC name.
			if (report == "ldb-dialogue")
			{
				await LdbDialogue(RdbDir(), arg);
				return 0;
			}

			// ONE capture, told as a story: --report-arg <capture>.tshark.json. Reads that dump directly rather than
			// the corpus, and wants the RDB only to decrypt the LDB text it quotes (it degrades without it).
			if (report == "timeline")
			{
				await Timeline(arg ?? "", RdbDir());
				return 0;
			}

			// One encrypted string, from a key pasted out of a capture: --report-arg <category>:<id>:<wire key>.
			if (report == "ldb-decrypt")
			{
				LdbDecryptOne(RdbDir(), arg);
				return 0;
			}

			// Adds or replaces ONE named fixture in the archive. Reads no captures, so it sits above the corpus check:
			// the hand-carved fixtures cannot be re-derived from the corpus, which is exactly why this exists.
			if (report == "fixture-set")
			{
				var split = (arg ?? "").IndexOf('=');
				if (split <= 0)
				{
					await Console.Error.WriteLineAsync("usage: --report fixture-set --report-arg <key>=<file>   e.g. golden/InitialBurst.bin=/tmp/burst.bin");
					await Console.Error.WriteLineAsync("       --report fixture-set --report-arg <key>=        removes it (an empty source, #555)");
					return 1;
				}

				var archive = Path.Combine("Faolan.Tests", "Assets", "fixtures.bin");
				var source = arg![(split + 1)..];

				// An empty source REMOVES the fixture - the path by which retail bytes leave the archive once a gate's
				// expectation has become a digest (#555). Spelled as the same flag rather than a second report so the
				// two halves of "what is in the archive" stay in one place.
				if (source.Length == 0)
					FixtureArchiveWriter.RemoveNamed(archive, arg[..split]);
				else
					FixtureArchiveWriter.SetNamed(archive, arg[..split], source);

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
				case "fixtures":
				{
					// The single file holding every byte the test suite reads: the corpus slice (rebuilt here) plus the
					// hand-carved named fixtures (carried over from the existing archive). See FixtureArchiveWriter.
					// Default paths here are CWD-relative: the importer runs from the repo root (see CLAUDE.md).
					var target = arg ?? Path.Combine("Faolan.Tests", "Assets", "fixtures.bin");
					FixtureArchiveWriter.Write(capturesDir, target);
					return 0;
				}

				case "conversation-states":
				{
					ConversationStates(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "handle-recycling":
				{
					HandleRecycling(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "shield-rose":
				{
					ShieldRose(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "dynel-cycle":
				{
					DynelCycle(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "chain-kill":
				{
					ChainKill(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "dynel-effects":
				{
					using var effectsDb = OpenDb();
					DynelEffects(NamedCapture.LoadAll(capturesDir), effectsDb);
					return 0;
				}

				case "dot-trains":
				{
					DotTrains(NamedCapture.LoadAll(capturesDir), arg, _options.Out);
					return 0;
				}

				case "buff-durations":
				{
					BuffDurations(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "buff-descriptor":
				{
					BuffDescriptor(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "buff-on-spawn":
				{
					BuffOnSpawn(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "coverage":
				{
					await Coverage(NamedCapture.LoadAll(capturesDir), arg, RdbDir());
					return 0;
				}

				case "vitals-shape":
				{
					VitalsShape(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "anim":
				{
					Anim(NamedCapture.LoadAll(capturesDir), arg ?? "Casilda");
					return 0;
				}

				case "player-actions":
				{
					// The ability catalog names the selectors: a selector IS an ability's Slot (#505).
					await using (var db = OpenDb())
						PlayerActions(NamedCapture.LoadAll(capturesDir), await db.Abilities.Where(a => a.Slot != 0).ToDictionaryAsync(a => a.Slot, a => a.Name), arg);

					return 0;
				}

				case "bcc-census":
				{
					BccCensus(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "bcc-attack-tail":
				{
					BccAttackTail(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "combo-swings":
				{
					await ComboSwings(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "equip":
				{
					Equip(NamedCapture.LoadAll(capturesDir), arg ?? "Casilda");
					return 0;
				}

				case "npc-damage":
				{
					NpcDamageCensus(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "npc-generic-stats":
				{
					NpcGenericStats(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "spawn-diff":
				{
					SpawnDiff(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "field-stability":
				{
					FieldStability(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "field-predictors":
				{
					FieldPredictors(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "scaffold-heap":
				{
					ScaffoldHeap(NamedCapture.LoadAll(capturesDir), arg, RdbDirOrNull());
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

				case "item-binding":
				{
					ItemBinding(NamedCapture.LoadAll(capturesDir), RdbDir());
					return 0;
				}

				case "spawn-item-level":
				{
					SpawnItemLevel(NamedCapture.LoadAll(capturesDir), _options.Out);
					return 0;
				}

				case "gem-slots":
				{
					GemSlots(RdbDir(), NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "acg-pair":
				{
					AcgPair(NamedCapture.LoadAll(capturesDir), RdbDir());
					return 0;
				}

				case "rdb-word-scan":
				{
					RdbWordScan(RdbDir(), arg);
					return 0;
				}

				case "region-stat-census":
				{
					RegionStatCensus(RdbDir());
					return 0;
				}

				case "class-attribute-runs":
				{
					ClassAttributeRuns(RdbDir(), _options.Out);
					return 0;
				}

				case "feat-effect-widths":
				{
					FeatEffectWidths(RdbDir(), arg);
					return 0;
				}

				case "feat-effect-kinds":
				{
					FeatEffectKinds(RdbDir(), arg);
					return 0;
				}

				case "feat-record":
				{
					FeatRecord(RdbDir(), arg);
					return 0;
				}

				case "feat-spell-modifiers":
				{
					FeatSpellModifiers(RdbDir(), arg);
					return 0;
				}

				case "feat-table":
				{
					FeatTable(RdbDir(), arg);
					return 0;
				}

				case "body-atlas-coverage":
				{
					BodyAtlasCoverage(RdbDir(), arg, options.Out);
					return 0;
				}

				case "spell-recast":
				{
					SpellRecast(RdbDir(), arg);
					return 0;
				}

				case "spell-refs":
				{
					SpellRefs(RdbDir(), arg);
					return 0;
				}

				case "spell-line-ids":
				{
					SpellLineIds(RdbDir(), arg);
					return 0;
				}

				case "spell-mana-mode":
				{
					SpellManaMode(RdbDir(), arg);
					return 0;
				}

				case "rdb-field-coverage":
				{
					RdbFieldCoverage(RdbDir(), arg, _options.Out);
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

				case "method-census":
				{
					MethodCensus(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "area-banner-names":
				{
					await AreaBannerNamesReport(RdbDir(), CapturedBanners(capturesDir));
					return 0;
				}
				case "creature-races":
				{
					await CreatureRaces();
					return 0;
				}

				case "create-classes":
				{
					CreateClasses(NamedCapture.LoadAll(capturesDir));
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

				case "resolve-spawn-areas":
				{
					await ResolveSpawnAreas(RdbDir());
					return 0;
				}

				case "spell-template":
				{
					var rdbDir = RdbDir();
					var ids = (arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
					using var rdb = OpenRdb(rdbDir);
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

						// The same region read as GAME ACTION ELEMENTS, addressed BY KEY. The pair view above is the
						// stat-modifier lens; several kinds carry no stat at all and are invisible through it - the
						// AoE radius (key 11 on the Area* kinds), the recast setters, the particle/script actions. A
						// spell's mechanics live here, so print every element with its kind name and named keys.
						foreach (var el in ItemTemplateStatModifiers.ParseElements(b.AsSpan(12)))
						{
							var type = GameActionTypes.Get(el.Kind);
							var keys = type == null
								? string.Join(" ", el.Values.Select((v, i) => $"[{i}]={v}"))
								: string.Join(" ", type.Keys.Select((k, i) => $"{k}={(i < el.Values.Length ? el.Values[i].ToString() : "-")}"));
							var texts = string.Join(" ", el.Texts.Where(t => !string.IsNullOrEmpty(t)).Select(t => $"\"{t}\""));
							Console.WriteLine($"      {el.Kind} {type?.Name ?? "?"}  {keys} {texts}".TrimEnd());
						}
					}

					return 0;
				}

				case "spell-stat-coverage":
				{
					var rdbDir = RdbDir();
					using var rdb = OpenRdb(rdbDir);
					int total = 0, withMods = 0, records = 0;
					var byStat = new Dictionary<uint, int>();
					foreach (var e in rdb.EntriesOfType(1040005).Where(rdb.CanRead))
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

				// `--report ldb-strings --report-arg "id:<text>"` searches the PARSED index and prints each hit's
				// (category, id) - as opposed to the default raw printable-run scan below, whose bracketed number is
				// a run ordinal and NOT an id. Confusing the two sent one investigation into page-base arithmetic on
				// numbers that were never in the id space; this is the form that answers "what id is this text".
				case "ldb-strings" when (arg ?? "").StartsWith("id:", StringComparison.OrdinalIgnoreCase):
				{
					// `id:<text>` searches by VALUE; `id:#<number>` searches by the id itself. The second is what tells
					// a "this id is gone" story from a "this id was never an LDB id" one.
					var wanted = arg![3..].Trim();
					uint wantedId = 0;
					var byNumber = wanted.StartsWith('#') && uint.TryParse(wanted[1..], out wantedId);
					using var rdb = OpenRdb(RdbDir());
					var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);
					var hits = 0;
					// A "not found" is only as strong as the coverage behind it, so the skips are COUNTED and printed.
					// An id missing from a search that silently skipped 300 encrypted pages is not missing.
					int searched = 0, unreadable = 0, notTdc2 = 0, encrypted = 0, entries = 0;
					var encryptedCategories = new SortedSet<uint>();
					foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType))
					{
						if (!rdb.CanRead(e))
						{
							unreadable++;
							continue;
						}

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

						if (LdbStringIndex.CategoryOf(body) == 0)
						{
							notTdc2++;
							continue;
						}

						if (LdbStringIndex.FlagsOf(body) == 1)
						{
							// The HEADER is plaintext even on an encrypted page, so the category is readable and the
							// skip can be reported per category - which is what says whether a miss in the category
							// you care about is conclusive.
							encrypted++;
							encryptedCategories.Add(LdbStringIndex.CategoryOf(body));
							continue;
						}

						searched++;

						foreach (var entry in LdbStringIndex.Parse(body))
						{
							entries++;
							var match = byNumber
								? entry.Id == wantedId
								: string.Equals(entry.Value, wanted, StringComparison.OrdinalIgnoreCase);
							if (!match)
								continue;

							hits++;
							Console.WriteLine($"  cat {entry.Category,-6} id {entry.Id,-12} page {e.Id,-9} lang {(languageByPage.TryGetValue(e.Id, out var lang) ? lang : "?")}  '{entry.Value}'");
						}
					}

					Console.WriteLine($"{hits} exact match(es) for '{wanted}'.");
					Console.WriteLine($"  searched {searched} page(s) / {entries:N0} entries; skipped {encrypted} encrypted, {notTdc2} non-TDC2, {unreadable} unreadable"
					                  + (encrypted + unreadable == 0 ? "  <-- complete over the plaintext LDB" : ""));
					if (encryptedCategories.Count > 0)
						Console.WriteLine($"  the skipped pages are categories {string.Join(", ", encryptedCategories)} - a miss is conclusive for any category NOT listed here");
					return 0;
				}
				case "ldb-strings":
				{
					var rdbDir = RdbDir();
					var needle = (arg ?? "damage").ToLowerInvariant();
					using var rdb = OpenRdb(rdbDir);
					foreach (var e in rdb.EntriesOfType(1030002).Where(rdb.CanRead))
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
					using var rdb = OpenRdb(rdbDir);
					var map = new Dictionary<uint, string>();
					var byCat = new Dictionary<(uint cat, uint id), string>();
					var perCategory = new Dictionary<uint, int>();
					// Per-page id->string for name categories, so we can crib-identify the ENGLISH page(s) against the
					// names our catalog already carries (#291/#300): 50210 spell names, 50200/50220 item names.
					var spellNamePages = new List<(uint page, Dictionary<uint, string> ids)>();
					var itemNamePages = new List<(uint cat, uint page, Dictionary<uint, string> ids)>();

					// The lookups: `--report-arg` takes a comma-separated list of raw ids AND/OR literal TOKEN NAMES.
					// A token is hashed with the client's own key function (LdbStringIndex.TokenId), which is what turns
					// a `FUN_0061f640(<cat>, "<Token>")` call found in the exe into the string it prints - statically,
					// with no live client. Resolved per PAGE rather than off `byCat`, because every category is shipped
					// once per language and the last page to write a slot wins: an unqualified lookup silently answers
					// in Korean.
					var wanted = (arg ?? "")
						.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						.ToDictionary(needle => uint.TryParse(needle, out var raw)
							? raw
							: LdbStringIndex.TokenId(needle), needle => needle);
					var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);
					var lookupHits = new List<(uint Id, uint Cat, uint Page, string Lang, string Value)>();

					var pages = 0;
					foreach (var (pageId, category, strings) in LdbPagesOf(rdb))
					{
						pages++;
						foreach (var (id, str) in strings)
						{
							map[id] = str;
							byCat[(category, id)] = str;
							if (wanted.ContainsKey(id))
								lookupHits.Add((id, category, pageId, languageByPage.GetValueOrDefault(pageId, "?"), str));
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

					if (wanted.Count > 0)
					{
						Console.WriteLine("=== lookups (English page first; a token that misses may just be spelled differently) ===");
						foreach (var (id, needle) in wanted)
						{
							var hits = lookupHits.Where(h => h.Id == id)
								.OrderByDescending(h => h.Lang == LdbLanguageIndex.English)
								.ThenBy(h => h.Cat)
								.ToList();
							var label = needle == id.ToString()
								? needle
								: $"{needle} -> {id}";
							if (hits.Count == 0)
							{
								Console.WriteLine($"  {label}: (no entry)");
								continue;
							}

							foreach (var hit in hits)
								Console.WriteLine($"  {label}  cat {hit.Cat} page {hit.Page} [{hit.Lang}]: '{hit.Value.ReplaceLineEndings(" ")}'");
						}
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
					await Console.Error.WriteLineAsync($"Unknown report '{report}'. Known: coverage, conversation-states, anim, worldobj, spell-fields, rdb-assets, ldb-strings, ldb-resolve, ldb-quest-text, quest-goal-targets, bounded-areas, rdb-quest-catalog.");
					return 1;
				}
			}
		}

		// Decrypts each Ability's RDB 1000020 entry with its stored Data1..4 TEA key (same scheme as item templates, #137)
		// and dumps the printable strings - the ability NAME lives in that (encrypted-but-key-available) template.
		private static void AbilityNames(string rdbDir)
		{
			using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);
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

				// Abilities are the SAME 1000020 template layout as items, spells and feats, so the shared parser
				// applies - but the answer is that they carry NO stat-modifier region. Of the 27 decryptable
				// abilities only 10 yield anything and all 10 are the parser's documented false-positive mode: the
				// "stats" are the directional ids the field table already shows (Upper Left Attack -> 201, Overhead
				// -> 203), or zero-valued (the four pet commands -> stats 1..4 = 0), or a kind-range number as an
				// amount (a mount ability -> 1890 = 53202). Their kinds (53043, 53251) are none of the flat/percent
				// tags either. Shown here so the emptiness is visible rather than assumed - do NOT import these.
				var mods = ItemTemplateStatModifiers.Parse(body.AsSpan(12));
				var modStr = mods.Count == 0
					? ""
					: "  | MODS " + string.Join(" ", mods.Select(m => $"{m.StatId}={m.WireAmount}(k{m.Kind})"));
				Console.WriteLine($"0x{a.Id:X8} slot={a.Slot,-5} \"{name}\"  | {fieldStr}{modStr}");
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
					var anim = body != null && SpawnBodyReader.TryReadCharacterMessage(body, out var read)
						? read.Message?.Bcc?.AnimIdleId.ToString() ?? "(no bcc)"
						: "(unreadable)";
					Console.WriteLine($"{c.Name[..Math.Min(40, c.Name.Length)]} | handle 0x{e.Handle:X} | AnimIdleId={anim} | pos=({e.X:0.#},{e.Z:0.#})");
				}
		}

		// #571: how often a single capture reuses one entity handle for MORE THAN ONE NPC, and what the spawn import
		// loses to it. Handles are recycled - a handle freed by a despawn is reissued later in the same session - but
		// the import treats a handle as a capture-scoped mob identity: `DistinctByHandle` keeps the FIRST spawn per
		// handle and silently drops every later one, and the patrol join used to hand the survivor the other NPC's
		// route (fixed in #565 by a positional sanity check, which is where this was found).
		//
		// ⚠️ Only a change of TEMPLATE proves it. The obvious-looking test - the same handle seen at two distant
		// positions - does NOT: an NPC that WANDERS or patrols streams in again wherever it has walked to, and over a
		// long session a cow covers a couple of hundred units. Counting distance-apart-only reuses reported 229 handles
		// where the provable figure is far smaller, and every inflated one was a Wild Cow / Conarch Guard seen at three
		// points of its own route. So distance is reported SEPARATELY as ambiguous and is never added to the total: a
		// different template on one handle is the only thing that cannot be one mob moving.
		private static void HandleRecycling(IReadOnlyList<NamedCapture> captures)
		{
			var provenHandles = 0;
			var provenLost = 0;
			var ambiguousHandles = 0;
			foreach (var capture in captures)
			{
				var byHandle = new Dictionary<uint, List<NpcSpawnEntry>>();
				foreach (var e in NpcSpawnExtractor.Extract(capture.Packets))
				{
					if (!byHandle.TryGetValue(e.Handle, out var list))
						byHandle[e.Handle] = list = [];

					if (!list.Exists(p => p.TemplateId == e.TemplateId))
						list.Add(e); // one entry per DISTINCT template on this handle
				}

				var proven = byHandle.Where(kv => kv.Value.Count > 1).ToList();
				var crossMap = proven.Count(kv => kv.Value.Select(e => e.MapId).Distinct().Count() > 1);
				if (proven.Count > 0)
					Console.WriteLine($"  ({crossMap} of {proven.Count} span more than one MAP)");
				ambiguousHandles += byHandle.Count(kv => kv.Value.Count == 1);
				if (proven.Count == 0)
					continue;

				var lost = proven.Sum(kv => kv.Value.Count - 1); // DistinctByHandle keeps the first, drops the rest
				provenHandles += proven.Count;
				provenLost += lost;
				Console.WriteLine($"{capture.Name[..Math.Min(60, capture.Name.Length)]} | handles proven recycled: {proven.Count} | spawns dropped: {lost}");
				foreach (var (handle, seen) in proven.OrderByDescending(kv => kv.Value.Count))
				{
					var who = seen.Select(e => $"map {e.MapId}: {e.Name ?? "?"}#{e.TemplateId}@({e.X:0.#},{e.Z:0.#})");
					Console.WriteLine($"    0x{handle:X8} -> {string.Join("  |  ", who)}");
				}
			}

			Console.WriteLine();
			Console.WriteLine($"TOTAL over {captures.Count} captures: {provenHandles} handle(s) PROVEN recycled (a second template on the same handle), "
			                  + $"costing {provenLost} spawn observation(s) to DistinctByHandle. {ambiguousHandles} single-template handle(s) were not counted - "
			                  + "a repeat sighting of one template is a mob that moved, not evidence of reuse.");
		}

		// --report dot-trains [--report-arg <spell name substring or base id>]
		//
		// The EVIDENCE behind every measured DoT tick interval: each `(spell, target, caster)` tick train the corpus
		// contains, how long it is, and the gaps it contributes - the exact input `DotIntervalEstimator` is handed.
		//
		// It exists because #524's open item is a spell whose gaps cluster on a LATTICE (Aura: Fire Weave, 17 gaps at
		// 0.92 / 1.23 / 1.53 / 1.84, spaced ~0.3s), and there are two very different explanations that the pooled gap
		// list cannot tell apart:
		//   * a HARMONIC - one long train of a ~0.3s pulse whose ticks are mostly unobserved, so every surviving gap
		//     is a multiple of the fundamental (the estimator's documented blind spot);
		//   * RE-APPLICATION phase - many SHORT trains, each contributing one arbitrary first-gap, in which case the
		//     lattice is an artefact of when the aura was recast and the spell simply has no measurable interval.
		// Train LENGTH separates them, and only this report shows it.
		private static void DotTrains(IReadOnlyList<NamedCapture> captures, string? filter, string? outPath)
		{
			var wanted = (filter ?? "").Trim();
			var trainLengths = new Dictionary<uint, List<int>>();
			var gapsBySpell = new Dictionary<uint, List<double>>();
			var names = new Dictionary<uint, string>();

			foreach (var capture in captures)
			{
				// Same key as CaptureStatDamageImporter: a train is one spell on one target from one caster, within
				// ONE capture (handles are session-volatile).
				var trains = new Dictionary<(uint Spell, uint Target, uint Caster), List<long>>();
				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: StatDamageCodec.Opcode })
						continue;

					if (!p.TryParse(StatDamageCodec.Parse, out var dmg) || dmg.SpellBaseId == 0 || dmg.Delta == 0)
						continue;

					var key = (dmg.SpellBaseId, dmg.Handle, dmg.Caster);
					trains.TryAdd(key, []);
					trains[key].Add(p.Ticks);
				}

				foreach (var (key, times) in trains)
				{
					times.Sort();
					trainLengths.TryAdd(key.Spell, []);
					trainLengths[key.Spell].Add(times.Count);
					if (times.Count < 2)
						continue;

					gapsBySpell.TryAdd(key.Spell, []);
					for (var i = 1; i < times.Count; i++)
						gapsBySpell[key.Spell].Add((times[i] - times[i - 1]) / 1e7);
				}
			}

			using var db = OpenDb();
			var ids = trainLengths.Keys.ToList();
			foreach (var spell in db.Spells.Where(s => ids.Contains(s.Id)))
				names[spell.Id] = spell.Name;

			Console.WriteLine($"{"spell",-42} {"trains",6} {"ticks",6} {"len1",5} {"len2",5} {"len3+",5} {"gaps",5}  estimate");
			foreach (var (spellId, lengths) in trainLengths.OrderByDescending(kv => kv.Value.Sum()))
			{
				var name = names.GetValueOrDefault(spellId, $"(uncatalogued {spellId})");
				if (wanted.Length > 0 && !name.Contains(wanted, StringComparison.OrdinalIgnoreCase) && spellId.ToString() != wanted)
					continue;

				var gaps = gapsBySpell.GetValueOrDefault(spellId, []);
				var estimate = DotIntervalEstimator.Estimate(gaps);
				Console.WriteLine($"{name[..Math.Min(42, name.Length)],-42} {lengths.Count,6} {lengths.Sum(),6} "
				                  + $"{lengths.Count(l => l == 1),5} {lengths.Count(l => l == 2),5} {lengths.Count(l => l >= 3),5} {gaps.Count,5}"
				                  + $"  {(estimate is { } ms ? $"{ms} ms" : "(none)")}");

				// The whole point when drilling one spell: the gaps WITH the train that produced each.
				if (wanted.Length == 0)
					continue;

				Console.WriteLine($"      train lengths: {string.Join(" ", lengths.OrderByDescending(l => l))}");
				Console.WriteLine($"      gaps: {string.Join(" ", gaps.OrderBy(g => g).Select(g => $"{g:0.00}"))}");
			}

			Console.WriteLine();
			Console.WriteLine("A train of length 2 contributes exactly ONE gap, and that gap is a re-application phase offset as often "
			                  + "as it is the period. A spell whose gaps come mostly from length-2 trains has no measured cadence, whatever "
			                  + "the pooled gaps look like (#524).");

			if (outPath == null)
				return;

			// `--out <file>` writes the SAME input at full precision, one (spell, gap) row per line. The console view
			// rounds to 2 dp, which is enough to read a cluster and not enough to re-run a candidate acceptance rule
			// against - and re-running one over every spell at once is the gate #657 asks any new rule to pass.
			var rows = new List<string> { "spellId\tname\tgapSeconds" };
			foreach (var (spellId, gaps) in gapsBySpell.OrderBy(kv => kv.Key))
			foreach (var gap in gaps.OrderBy(g => g))
				rows.Add($"{spellId}\t{names.GetValueOrDefault(spellId, "")}\t{gap:R}");

			File.WriteAllLines(outPath, rows);
			Console.WriteLine($"wrote {outPath} ({rows.Count - 1} gaps)");
		}

		// --report chain-kill
		//
		// Every `ChainKillIIR` in the corpus in its own timeline, against the kill counters that move with it: stat
		// `0x6A` (106) and `0xF4256` (1000070), which #446 read as "remaining of 20" and "achieved". Prints each
		// counter move and each ChainKill, with the seconds since the previous one of each.
		//
		// #446's open questions are all TIMING questions - "what exactly triggers the IIR: N kills within a time
		// window? Both arrivals are ~9-17s apart, so a window is plausible but unmeasured" - and it proposed a fresh
		// retail session to settle them. The corpus already carries the records; this reads them.
		//
		// ⚠️ It does NOT need the IIR body decoded, and the body is not the gap: `ChainKillCodec` already reads it
		// byte-exact off the client's own reader (handle, env, count). What is missing is when to SEND one.
		private static void ChainKill(IReadOnlyList<NamedCapture> captures)
		{
			const uint chainCounterA = 0x6A; // 106 - "remaining of 20" in #446's reading
			const uint chainCounterB = 0xF4256; // 1000070 - DailyPvPKills per the client's stat-name table
			const uint chainCounterC = 0xF4255; // 1000069 - DailyPvEKills, its sibling

			var counts = new List<uint>();
			var gaps = new List<double>();
			var resetDelays = new List<double>();   // non-zero -> the 0 that closes it
			var escalations = new List<double>();   // non-zero -> a HIGHER non-zero
			var withACounterMove = 0;
			foreach (var capture in captures)
			{
				var lines = new List<string>();
				long? previousChain = null;
				var previousCount = 0u;
				long? previousCounter = null;
				var counterMovesSinceChain = 0;
				var recent = new List<(long Ticks, uint Type)>();

				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true })
						continue;

					recent.Add((p.Ticks, p.SubOpcode));
					if (recent.Count > 400)
						recent.RemoveRange(0, 200);

					if (p.SubOpcode == StatUpdateCodec.Opcode)
					{
						if (!p.TryParse(StatUpdateCodec.Parse, out var update))
							continue;

						var moved = update.Stats.Records
							.Where(r => (uint)r.StatId is chainCounterA or chainCounterB or chainCounterC)
							.ToList();
						if (moved.Count == 0)
							continue;

						counterMovesSinceChain++;
						var since = previousCounter is { } pc ? $"{TimeSpan.FromTicks(p.Ticks - pc).TotalSeconds,6:0.0}s" : "     -";
						previousCounter = p.Ticks;
						lines.Add($"    counter  {since} since previous   0x{update.EntityHandle:X8}  "
						          + string.Join("  ", moved.Select(r => $"{(uint)r.StatId}={r.RawValue}")));
						continue;
					}

					if (p.SubOpcode != ChainKillCodec.Opcode || !p.TryParse(ChainKillCodec.Parse, out var chain))
						continue;

					var gap = previousChain is { } pk ? TimeSpan.FromTicks(p.Ticks - pk).TotalSeconds : double.NaN;
					if (!double.IsNaN(gap))
					{
						gaps.Add(gap);
						if (chain.Count == 0 && previousCount > 0)
							resetDelays.Add(gap);
						else if (chain.Count > previousCount && previousCount > 0)
							escalations.Add(gap);
					}

					counts.Add(chain.Count);
					if (counterMovesSinceChain > 0)
						withACounterMove++;

					// What arrived in the two seconds before it - the trigger names itself if anything does.
					var before = recent
						.Where(r => TimeSpan.FromTicks(p.Ticks - r.Ticks).TotalSeconds <= 2.0)
						.GroupBy(r => r.Type)
						.OrderByDescending(g => g.Count())
						.Select(g => $"{IirName(g.Key)}x{g.Count()}")
						.Take(8);

					lines.Add($"  CHAINKILL {(double.IsNaN(gap) ? "     -" : $"{gap,6:0.0}s")} since previous   0x{chain.Handle:X8}  "
					          + $"count={chain.Count}  env=0x{chain.Env:X2}  ({counterMovesSinceChain} counter move(s) since the last chain)");
					lines.Add($"              preceded within 2s by: {string.Join(" ", before)}");
					previousChain = p.Ticks;
					previousCount = chain.Count;
					counterMovesSinceChain = 0;
				}

				if (!lines.Exists(l => l.Contains("CHAINKILL", StringComparison.Ordinal)))
					continue;

				Console.WriteLine(capture.Name);
				foreach (var line in lines)
					Console.WriteLine(line);
			}

			Console.WriteLine();
			Console.WriteLine($"TOTAL: {counts.Count} ChainKillIIR record(s) over {captures.Count} captures.");
			if (counts.Count > 0)
			{
				Console.WriteLine("  count values: " + string.Join(", ", counts.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}")));
				Console.WriteLine($"  arrivals preceded by a kill-counter move: {withACounterMove} of {counts.Count}");
			}

			if (gaps.Count > 0)
			{
				var sorted = gaps.OrderBy(g => g).ToList();
				Console.WriteLine($"  gap between consecutive chains: min={sorted[0]:0.0}s median={sorted[sorted.Count / 2]:0.0}s max={sorted[^1]:0.0}s");
			}

			Describe("reset  (non-zero -> 0)", resetDelays);
			Describe("escalate (n -> n+1)", escalations);
			return;

			static void Describe(string what, List<double> values)
			{
				if (values.Count == 0)
					return;

				var sorted = values.OrderBy(v => v).ToList();
				Console.WriteLine($"  {what}: {sorted.Count} sample(s), min={sorted[0]:0.00}s median={sorted[sorted.Count / 2]:0.00}s max={sorted[^1]:0.00}s");
			}

			static string IirName(uint type)
			{
				return Enum.IsDefined(typeof(IirTypes), type) ? ((IirTypes)type).ToString() : $"0x{type:X8}";
			}
		}

		// --report dynel-cycle [--report-arg <minimum burst size, default 4>]
		//
		// Retail's world-object interest, measured: every BURST of `SimpleDynelFullUpdateIIR` spawns or
		// `n3ToClientQuitIIR` despawns that arrives in one second, with the burst's centroid, the player's own
		// position at that moment (from their last uploaded `CharDCMove`) and the distance between the two.
		//
		// #529 is the question it answers. Twelve Stone Bricks were seen despawning at 83u, respawning at 144u and
		// despawning again at 149u - an ordering no radius test can produce, so whatever drives retail's streaming is
		// not the distance our `InterestManager` uses. One capture cannot say what it IS; a census of every bulk
		// event in the corpus can, because a RADIUS leaves a tight band of distances and a grid/cell scheme does not.
		//
		// Bursts, not individual events, because that is the unit of the phenomenon: an interest pass moves a whole
		// group at once, and a single spawn is just a mob walking into view.
		// --report dynel-effects
		//
		// Every `SimpleDynelVisualChangeIIR` PARTICLE record in the corpus, attributed to the object it played on and
		// crossed against the effect that object's own geometry declares (#698/#687). Written because the two
		// questions this family raises cannot be answered from a count: whether a record is a standing decoration or
		// a one-off puff (the ATTRACTOR says), and whether we already hold its effect (the geometry node says).
		//
		// ⚠️ Handles are RECYCLED - the same handle is a `Location Object - Walls` early in a capture and an Oak Tree
		// later - so a record is attributed through the SPAWN that most recently minted its handle, within one
		// capture. Keying on the handle alone merges unrelated objects and was worth an hour on #494.
		private static void DynelEffects(IReadOnlyList<NamedCapture> captures, DatabaseContext db)
		{
			// What the objects' own geometry declares: effect id -> how many nodes carry it (#687).
			var declared = db.WorldObjectWords
				.Where(w => w.Kind == WorldObjectWordKind.GeometryNode && w.Ordinal % 6 == 0)
				.AsEnumerable()
				.GroupBy(w => w.Value)
				.ToDictionary(g => g.Key, g => g.Count());

			var starts = new List<(string Capture, uint Handle, string Name, uint Effect, uint Attractor, bool AfterSpawn)>();
			var stopped = new List<(string Capture, uint Handle, string Name, uint TypeTag, uint Word, bool HadStart)>();
			foreach (var capture in captures)
			{
				var namesByHandle = new Dictionary<uint, string>();
				var spawnedInThisCapture = new HashSet<uint>();
				foreach (var p in capture.Packets)
				{
					if (!p.IsIirCarrier)
						continue;

					// The spawn that minted the handle - and it re-mints, so the LAST one before a record wins. Every
					// class of the dynel family carries the same identity message in the same place, which is why one
					// codec read plus WorldObjectColumns covers all of them.
					if (WorldObjectClasses.Placed.Contains(p.SubOpcode) && p.TryParse(WorldObjectCodec.Parse, out var obj))
					{
						namesByHandle[obj.Handle] = WorldObjectColumns.TryReadIdentity(p.Record, out var identity) ? identity.Name ?? "" : "";
						spawnedInThisCapture.Add(obj.Handle);
						continue;
					}

					if (p.SubOpcode != SimpleDynelVisualChangeCodec.Opcode || !p.TryParse(SimpleDynelVisualChangeCodec.Parse, out var change))
						continue;

					if (change.Kind == (uint)DynelVisualChange.StopParticleEffect)
					{
						stopped.Add((capture.Name, change.Handle, namesByHandle.GetValueOrDefault(change.Handle, "<no spawn seen>"), change.TypeTag,
							change.Words.Count > 0 ? change.Words[0] : 0, starts.Any(x => x.Handle == change.Handle && x.Capture == capture.Name)));
					}

					if (change.Kind != (uint)DynelVisualChange.StartParticleEffect || change.Words.Count < 2)
						continue;

					starts.Add((capture.Name, change.Handle, namesByHandle.GetValueOrDefault(change.Handle, "<no spawn seen>"),
						change.Words[0], change.Words[1], spawnedInThisCapture.Contains(change.Handle)));
				}
			}

			Console.WriteLine($"{starts.Count} StartParticleEffect record(s), {stopped.Count} Stop, over {captures.Count} capture(s)");
			Console.WriteLine();
			Console.WriteLine("  attractor 0 = FREE-STANDING at the given position (the harvest puff); 34/35 = bolted to a bone (a standing decoration).");
			foreach (var g in starts.GroupBy(s => s.Attractor).OrderByDescending(g => g.Count()))
				Console.WriteLine($"    attractor {g.Key,3}: {g.Count(),4} record(s), {g.Select(s => s.Effect).Distinct().Count()} distinct effect(s)");

			Console.WriteLine();
			Console.WriteLine("  effect        records  attractor  in a geometry node?  played on");
			foreach (var g in starts.GroupBy(s => s.Effect).OrderByDescending(g => g.Count()))
			{
				var held = declared.TryGetValue(g.Key, out var nodes) ? $"YES ({nodes} node(s))" : "no";
				var on = string.Join(", ", g.Select(s => s.Name).Distinct().Take(3));
				Console.WriteLine($"  {g.Key,-10} {g.Count(),8}  {string.Join("/", g.Select(s => s.Attractor).Distinct()),9}  {held,-19}  {on}");
			}

			// The STOPs, with what they played on. A Stop that never had a Start in the same capture is retail
			// clearing an effect the object brought with it - which is the shape a loot bag's sparkle would have.
			Console.WriteLine();
			Console.WriteLine("  StopParticleEffect, by the object it was sent to:");
			foreach (var g in stopped.GroupBy(x => (x.TypeTag, x.Name)).OrderByDescending(g => g.Count()))
				Console.WriteLine($"    class 0x{g.Key.TypeTag:X4}  {g.Key.Name,-40} {g.Count(),3} record(s), {g.Count(x => x.HadStart)} of them after a Start in the same capture");

			// ⭐ The cross-tab that says what an effect IS. A decoration is started and left running; a STATE marker
			// is started and later stopped on the same handle. Crossed against whether the object's own geometry
			// declares the effect, the two populations separate.
			Console.WriteLine();
			Console.WriteLine("  effect      starts  stopped-on-the-same-handle  declared-in-geometry");
			foreach (var g in starts.GroupBy(x => x.Effect).OrderByDescending(g => g.Count()))
			{
				var handles = g.Select(x => (x.Capture, x.Handle)).ToHashSet();
				var alsoStopped = stopped.Count(x => handles.Contains((x.Capture, x.Handle)));
				Console.WriteLine($"  {g.Key,-10} {g.Count(),6}  {alsoStopped,26}  {(declared.ContainsKey(g.Key) ? "yes" : "NO"),20}");
			}

			var missing = starts.Where(s => s.Attractor != 0 && !declared.ContainsKey(s.Effect)).ToList();
			Console.WriteLine();
			Console.WriteLine($"  STANDING effects with no geometry node of ours: {missing.Select(s => s.Effect).Distinct().Count()} distinct over {missing.Count} record(s) (#698)");
			Console.WriteLine($"  of those records, {missing.Count(s => s.AfterSpawn)} played on an object whose SPAWN is in the same capture");
		}

		private static void DynelCycle(IReadOnlyList<NamedCapture> captures, string? arg)
		{
			var minimumBurst = int.TryParse(arg, out var m) && m > 0 ? m : 4;
			var spawnDistances = new List<double>();
			var despawnDistances = new List<double>();
			// The WORLD-ENTRY burst is a world BUILD, not an interest pass - it states the playfield the player just
			// loaded into, so it reaches as far as the playfield does. Scored separately, because leaving it in is
			// what makes the radius verdict read as "something spawned 1834u away" when nothing was streaming at all.
			var midSessionSpawnDistances = new List<double>();
			var midSessionDespawnDistances = new List<double>();
			var refreshBursts = 0;
			var despawnBursts = 0;
			var playerAtBurst = new List<Vector3>();
			var playerEverywhere = new List<Vector3>();

			foreach (var capture in captures)
			{
				// A dynel despawn names only a handle, so its position has to come from the spawn that introduced it.
				var positions = new Dictionary<uint, Vector3>();
				var player = Vector3.Zero;
				var seenPlayer = false;
				var bursts = new List<(long Ticks, bool Spawn, List<uint> Handles, Vector3 Player)>();

				foreach (var p in capture.Packets)
				{
					if (!p.IsIirCarrier)
						continue;

					// The player's own position: their UPLOADED moves, which is the one CharDCMove stream that is
					// certainly the capturing character (the downloads are every visible mover).
					if (p is { IsDownload: false, SubOpcode: CharDCMoveCodec.Opcode })
					{
						if (p.TryParse(CharDCMoveCodec.Parse, out var move))
						{
							player = move.Position;
							seenPlayer = true;
							playerEverywhere.Add(player); // the control sample for the cell-boundary fit below
						}

						continue;
					}

					if (!p.IsDownload || !seenPlayer)
						continue;

					uint handle;
					bool spawn;
					if (p.SubOpcode == WorldObjectClasses.SimpleDynel)
					{
						if (!p.TryParse(WorldObjectCodec.Parse, out var obj))
							continue;

						handle = obj.Handle;
						positions[handle] = obj.Transform.FromProtobuf<ProtoGameCodeDynelIIR>().Position;
						spawn = true;
					}
					else if (p.SubOpcode == N3ToClientQuitCodec.Opcode)
					{
						if (!p.TryParse(N3ToClientQuitCodec.Parse, out var quit) || !positions.ContainsKey(quit.Handle))
							continue; // only the dynels we saw spawn - a character quit is a different event

						handle = quit.Handle;
						spawn = false;
					}
					else
					{
						continue;
					}

					var last = bursts.Count > 0 ? bursts[^1] : default;
					if (bursts.Count > 0 && last.Spawn == spawn && TimeSpan.FromTicks(p.Ticks - last.Ticks).TotalSeconds <= 1.0)
						last.Handles.Add(handle);
					else
						bursts.Add((p.Ticks, spawn, [handle], player));
				}

				var interesting = bursts.Where(b => b.Handles.Count >= minimumBurst).ToList();
				if (interesting.Count == 0)
					continue;

				// A despawn burst is a REFRESH when most of what it dropped comes straight back. #529 read the Stone
				// Bricks' "removed and restored together" as an interest cycle; if the same handles return within
				// seconds, it is not interest at all - it is the cell being re-sent, and the distances at each end are
				// just wherever the player had walked to by then.
				var refreshWindow = TimeSpan.FromSeconds(30).Ticks;
				foreach (var burst in interesting.Where(b => !b.Spawn))
				{
					var dropped = burst.Handles.ToHashSet();
					var returned = interesting
						.Where(b => b.Spawn && b.Ticks > burst.Ticks && b.Ticks - burst.Ticks <= refreshWindow)
						.SelectMany(b => b.Handles)
						.ToHashSet();
					returned.IntersectWith(dropped);
					despawnBursts++;
					if (returned.Count * 2 >= dropped.Count)
						refreshBursts++;
				}

				Console.WriteLine($"{capture.Name}");
				var start = interesting[0].Ticks;
				foreach (var burst in interesting)
				{
					var known = burst.Handles.Where(positions.ContainsKey).Select(h => Vector3.Distance(positions[h], burst.Player)).ToList();
					if (known.Count == 0)
						continue;

					// The RADIUS test is one-sided per kind, and that is the whole trick. A radius R bounds a SPAWN
					// burst from above (nothing past R may appear) and a DESPAWN burst from below (nothing inside R
					// may vanish). So the numbers that matter are the FARTHEST thing that spawned and the NEAREST
					// thing that despawned - if those two ranges overlap, no single R can produce the corpus.
					var extreme = burst.Spawn ? known.Max() : known.Min();
					(burst.Spawn ? spawnDistances : despawnDistances).Add(extreme);
					if (burst.Ticks != start)
						(burst.Spawn ? midSessionSpawnDistances : midSessionDespawnDistances).Add(extreme);
					playerAtBurst.Add(burst.Player);

					Console.WriteLine($"  t+{TimeSpan.FromTicks(burst.Ticks - start).TotalSeconds,7:0.0}s  {(burst.Spawn ? "SPAWN  " : "DESPAWN")}"
					                  + $" x{burst.Handles.Count,3}  player=({burst.Player.X,8:0.#},{burst.Player.Z,8:0.#})"
					                  + $"  {(burst.Spawn ? "farthest spawned" : "nearest despawned")}={extreme,7:0.#}"
					                  + $"  (near={known.Min(),7:0.#} far={known.Max(),7:0.#})");
				}
			}

			Console.WriteLine();
			Report("SPAWN   farthest-spawned", spawnDistances);
			Report("DESPAWN nearest-despawned", despawnDistances);
			Console.WriteLine();
			Console.WriteLine("Excluding each capture's WORLD-ENTRY burst (a world build, not an interest pass):");
			Report("SPAWN   farthest-spawned", midSessionSpawnDistances);
			Report("DESPAWN nearest-despawned", midSessionDespawnDistances);
			if (despawnBursts > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"REFRESH: {refreshBursts} of {despawnBursts} despawn burst(s) ({100.0 * refreshBursts / despawnBursts:0.#}%) had most of what they");
				Console.WriteLine("  dropped re-spawned within 30s - i.e. the cell was RE-SENT rather than the objects losing interest.");
			}

			Console.WriteLine();
			Console.WriteLine("A single interest RADIUS R would need every 'farthest spawned' <= R <= every 'nearest despawned'.");
			if (spawnDistances.Count > 0 && despawnDistances.Count > 0)
			{
				var floor = despawnDistances.Min();
				var ceiling = spawnDistances.Max();
				Console.WriteLine(floor >= ceiling
					? $"  -> consistent: any R in [{ceiling:0.#}, {floor:0.#}] fits."
					: $"  -> IMPOSSIBLE: something spawned at {ceiling:0.#} while something else despawned at {floor:0.#}. "
					  + $"{spawnDistances.Count(d => d > floor)} spawn burst(s) reach past the nearest despawn.");
			}

			// The grid hypothesis #529 proposes. If interest is per CELL, a burst fires when the player CROSSES a cell
			// boundary, so the player's position at a burst should sit near a multiple of the cell size on one axis.
			//
			// ⚠️ The null model is NOT `cell/6` (the value a uniformly placed player would give). It is the SAME
			// statistic over every position the player was ever at, because a player does not walk uniformly: they
			// stand on designer-placed round coordinates, and a burst-only score has no meaning without that control.
			// A real cell size beats the walk; a spurious one matches it.
			Console.WriteLine();
			Console.WriteLine("Cell-boundary fit - player position at each burst, CONTROLLED against every position they walked:");
			foreach (var cell in new[] { 16f, 32f, 50f, 64f, 100f, 128f, 160f, 200f, 256f, 320f, 512f })
			{
				var atBursts = Median(playerAtBurst, cell);
				var whileWalking = Median(playerEverywhere, cell);
				if (double.IsNaN(atBursts) || double.IsNaN(whileWalking))
					continue;

				Console.WriteLine($"  cell {cell,5:0}: median offset at a burst {atBursts,7:0.#}   while walking {whileWalking,7:0.#}"
				                  + (atBursts < whileWalking * 0.6 ? "   <- FIT" : ""));
			}

			return;

			// DISTINCT positions on both sides: bursts arrive in clumps at one standing position (a map change fires
			// several at the same coordinates), and counting that position once per burst would let a single spot
			// carry the whole statistic.
			static double Median(List<Vector3> points, float cell)
			{
				var offsets = points
					.Distinct()
					.Select(p => Math.Min(BoundaryDistance(p.X, cell), BoundaryDistance(p.Z, cell)))
					.OrderBy(d => d)
					.ToList();
				return offsets.Count == 0 ? double.NaN : offsets[offsets.Count / 2];
			}

			static float BoundaryDistance(float value, float cell)
			{
				var offset = value - MathF.Floor(value / cell) * cell;
				return MathF.Min(offset, cell - offset);
			}

			static void Report(string kind, List<double> distances)
			{
				var usable = distances.Where(d => !double.IsNaN(d)).OrderBy(d => d).ToList();
				if (usable.Count == 0)
				{
					Console.WriteLine($"{kind}: no bursts.");
					return;
				}

				Console.WriteLine($"{kind}: {usable.Count} burst(s), min={usable[0]:0.#} "
				                  + $"p25={usable[usable.Count / 4]:0.#} median={usable[usable.Count / 2]:0.#} "
				                  + $"p75={usable[usable.Count * 3 / 4]:0.#} max={usable[^1]:0.#}");
			}
		}

		// --report shield-rose
		//
		// Every `CombatShieldIIR` in the corpus as a per-defender timeline: when each rose arrived, what it said, how
		// long since that handle's previous rose, and how long since that handle's SPAWN (`SimpleCharFullUpdateIIR`)
		// in the same capture.
		//
		// The last column is the whole point (#622). Retail leaves the spawn scaffold's shield word at 0, so a viewer
		// that arrives after a defender's rose was set has nothing to draw - unless retail sends a CATCH-UP rose when
		// the entity comes into view. If it does, this report shows it as a rose arriving within a second or two of
		// that handle's spawn; if the only roses are ones that follow a previous rose by the measured 5-12s shift
		// cadence, retail has the same hole and inventing a catch-up send would be inventing a message.
		//
		// A rose that repeats the PREVIOUS VALUE is called out for the same reason: a shift moves exactly one shield
		// (34 of 39 captured transitions), so an unchanged re-send is not a shift and wants explaining.
		private static void ShieldRose(IReadOnlyList<NamedCapture> captures)
		{
			var total = 0;
			var afterSpawn = 0;
			var unchanged = 0;
			var noSpawnSeen = 0;
			foreach (var capture in captures)
			{
				var spawnTicks = new Dictionary<uint, long>();
				var previous = new Dictionary<uint, (long Ticks, uint Value)>();
				var lines = new List<string>();

				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true })
						continue;

					// Both records put the subject handle at the same place - body is [u32 recordLen][u32 type][u32 ...]
					// [u32 handle] - so the spawn side needs no full parse (most spawn bodies would not survive one).
					if (p.SubOpcode == SimpleCharFullUpdateCodec.Opcode)
					{
						if (p.Body.Length >= 16)
							spawnTicks[p.Body.ReadU32Be(12)] = p.Ticks;
						continue;
					}

					if (p.SubOpcode != CombatShieldCodec.Opcode || p.Body.Length < sizeof(uint))
						continue;

					CombatShieldIIR rose;
					try
					{
						rose = CombatShieldCodec.Parse(p.Body.AsSpan(sizeof(uint)));
					}
					catch (Exception)
					{
						continue;
					}

					total++;
					var sinceSpawn = spawnTicks.TryGetValue(rose.Handle, out var spawned)
						? TimeSpan.FromTicks(p.Ticks - spawned).TotalSeconds
						: double.NaN;
					var sincePrevious = previous.TryGetValue(rose.Handle, out var last)
						? TimeSpan.FromTicks(p.Ticks - last.Ticks).TotalSeconds
						: double.NaN;

					var flags = "";
					if (double.IsNaN(sinceSpawn))
						noSpawnSeen++;
					else if (double.IsNaN(sincePrevious) && sinceSpawn <= 2.0)
					{
						afterSpawn++;
						flags += " CATCH-UP?"; // a FIRST rose hard on the heels of the spawn is what a catch-up looks like
					}

					if (!double.IsNaN(sincePrevious) && last.Value == rose.Value)
					{
						unchanged++;
						flags += " UNCHANGED";
					}

					lines.Add($"    0x{rose.Handle:X8}  value={rose.Value:000}  +{Fmt(sincePrevious)}s since previous  +{Fmt(sinceSpawn)}s since spawn{flags}");
					previous[rose.Handle] = (p.Ticks, rose.Value);
				}

				if (lines.Count == 0)
					continue;

				Console.WriteLine($"{capture.Name}  ({lines.Count} rose record(s))");
				foreach (var line in lines)
					Console.WriteLine(line);
			}

			Console.WriteLine();
			Console.WriteLine($"TOTAL over {captures.Count} captures: {total} CombatShieldIIR record(s).");
			Console.WriteLine($"  first rose within 2s of that handle's spawn (a catch-up would look like this): {afterSpawn}");
			Console.WriteLine($"  re-sends of the PREVIOUS value (not a shift):                                  {unchanged}");
			Console.WriteLine($"  no spawn for that handle in the same capture:                                  {noSpawnSeen}");
			return;

			static string Fmt(double seconds)
			{
				return double.IsNaN(seconds) ? "  -  " : $"{seconds,5:0.0}";
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
				using var reader = OpenRdb(rdb);
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
						$"{await db.Feats.CountAsync():N0} feats · " +
						$"{await db.ConversationNodes.CountAsync():N0} conversation nodes · {await db.Shops.CountAsync():N0} shops · {await db.QuestDefinitions.CountAsync():N0} quests · " +
						$"{await db.WorldObjects.CountAsync():N0} world objects · {await db.PatrolNodes.Where(n => n.ParentPath == null).CountAsync():N0} patrol routes · " +
						$"{await db.MapRegions.CountAsync():N0} client trigger volumes";
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
		// THE COMBAT-BALANCE MEASUREMENT TOOL. CombatResolver's MEASURED constants come from this report, and its
		// class header explains how to read the output; docs/subsystems/combat.md carries the same summary.
		//
		// What retail's combat actually DEALT, by NPC level (#401/#20). The captures carry every floating number as a
		// DamageResultIIR [target][attacker][damage][school], and the spawn extractor gives each NPC handle its level
		// - so joining them measures both directions instead of guessing:
		//   NPC -> player  : the curve ResolveNpcAttackDamage approximates (level * 100, plus the #488 level-gap scale)
		//   player -> NPC  : what a player's hit lands for on a level-N NPC, i.e. the shape any NPC-armour model has
		//                    to reproduce
		// ⚠️ Both sides are POST-mitigation - they are what the client was told to draw - so they are the target
		// numbers, not raw weapon rolls.
		private static void NpcDamageCensus(IReadOnlyList<NamedCapture> captures)
		{
			var scaleRatios = new Dictionary<double, int>();
			var killRatios = new Dictionary<double, int>();
			var hitsToKill = new Dictionary<uint, List<double>>();
			var fromNpc = new Dictionary<(uint Level, DamageSchool School), List<uint>>();
			var fromNpcSpell = new Dictionary<(uint Level, DamageSchool School), List<uint>>();
			var toNpcSpell = new Dictionary<(uint Level, DamageSchool School), List<uint>>();
			var toNpc = new Dictionary<(uint Level, DamageSchool School), List<uint>>();

			// Per-ATTACKER melee, the same hits as `fromNpc` but not collapsed into a level bucket (#479). A level
			// bucket mixes archetypes - Poitain carries a 304,944-HP `Poitain Guard` and a 4,605-HP
			// `Zingaran Alchemist` BOTH at level 80 - and the combat log proves how far apart they hit: a
			// `Zingaran Stockpiler` at level 70/72 swings for 7-14 displayed while a `Hired Bodyguard` at 75 swings
			// for 67-92, ~8x at +3 levels. Collapsed by level that reads as a sudden cliff at 74/75 and invites a
			// curve to be fitted through it. `hitsToKill` below already makes this caveat; the melee table did not.
			var fromNpcByAttacker = new Dictionary<(uint Template, uint Level), List<uint>>();
			var attackerHealth = new Dictionary<(uint Template, uint Level), uint>();
			// level -> every captured spawn health at that level (the `hp/curve` denominator, see below).
			var healthByLevel = new Dictionary<uint, List<uint>>();
			var attackerNames = new Dictionary<uint, string>();
			var attackerPlayerLevel = new Dictionary<(uint Template, uint Level), uint>();

			foreach (var c in captures)
			{
				// handle -> level, for every NPC this capture spawned.
				var levels = new Dictionary<uint, uint>();
				var spawnHealth = new Dictionary<uint, uint>();
				var names = new Dictionary<uint, string>();
				var templates = new Dictionary<uint, uint>();
				var playerLevels = new Dictionary<uint, uint>();
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
				{
					var spawnBody = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
						: null;
					if (spawnBody != null && SpawnBodyReader.TryReadCharacterMessage(spawnBody, out var read) && read.Message.Stats is { } st)
					{
						levels[e.Handle] = st.Level;
						if (!string.IsNullOrEmpty(e.Name))
							names[e.Handle] = e.Name;
						templates[e.Handle] = e.TemplateId;
						if (st.Health > 0)
						{
							spawnHealth[e.Handle] = st.Health;
							// The level's health CURVE, from every captured spawn rather than only the ones that hit
							// the player. Built off the attackers alone it was a median of one to three values, so
							// nearly every row read exactly 1.0 - a column that cannot separate an elite from a
							// critter is worse than no column, because it looks like it did (#671).
							(healthByLevel.TryGetValue(st.Level, out var hl) ? hl : healthByLevel[st.Level] = []).Add(st.Health);
						}
					}
				}

				// PLAYER levels, PER HANDLE. NpcSpawnExtractor drops the player's spawn (handle >= the character
				// instance base), which is exactly the record that carries it, so read it straight off the raw spawns.
				// Needed because the interesting variable is not the mob's level but the GAP to whoever it HIT - see
				// the `lvl gap` column below.
				//
				// ⚠️ Kept per handle rather than collapsed to the capture's MAXIMUM, which is what it used to be.
				// A capture is not one player: any level-80 stranger who walks past is spawned into it, and the max
				// then stamped `pc lvl 80` onto every mob in the file. Every one of #671's "elite" outliers is a
				// low-level mob scored against a gap it never fought, so the whole elite question was an artifact of
				// this line (#671).
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					var sb = p.Body;
					if (sb.Length < 20 || sb.ReadU32Be(12) < EntityHandles.CharacterInstanceBase)
						continue;

					// `Body` is already past the packet prologue+header, so it IS the IIR body the reader wants -
					// unlike NpcSpawnEntry.Data above, which is the whole packet and has to be sliced.
					if (SpawnBodyReader.TryReadCharacterMessage(sb, out var pread) && pread.Message.Stats is { Level: > 0 } pst)
						playerLevels[sb.ReadU32Be(12)] = pst.Level;
				}

				// Track each entity's last-seen HealthCurrent from the stat updates, so a hit can be compared with the
				// health drop that follows it - the only way to settle whether the damage number shares the vitals'
				// x100 wire scale or is the raw displayed figure.
				var health = new Dictionary<uint, uint>();
				var pendingHits = new Dictionary<uint, DamageResultIIR>(); // per TARGET: other packets interleave
				var dealtTo = new Dictionary<uint, uint>();             // total damage each NPC took, for the scale check

				foreach (var p in c.Packets)
				{
					if (!p.IsIirCarrier || p.Body is not { Length: >= 8 })
						continue;

					if (p.Body.ReadU32Be(4) == StatUpdateCodec.Opcode && p.Body.Length >= 29)
					{
						var handle = p.Body.ReadU32Be(12);
						var count = p.Body[20];
						for (var k = 0; k < count && 21 + k * 8 + 8 <= p.Body.Length; k++)
						{
							if (p.Body.ReadU32Be(21 + k * 8) != (uint)CharacterStatId.HealthCurrent)
								continue;

							var now = p.Body.ReadU32Be(21 + k * 8 + 4);
							if (pendingHits.Remove(handle, out var prior) && health.TryGetValue(handle, out var before) && before > now)
							{
								var ratio = (double)(before - now) / prior.Damage;
								scaleRatios[Math.Round(ratio, 2)] = scaleRatios.GetValueOrDefault(Math.Round(ratio, 2)) + 1;
							}

							health[handle] = now;
						}

						continue;
					}

					// ⚠️ SPELL damage is NOT a DamageResult - it rides StatDamageIIR (see StatDamageCodec / #349),
					// so a census of DamageResult alone reports "no magical schools" and means only "I looked in the
					// melee packet". A caster NPC's hit is a negative HealthCurrent delta with the caster's handle.
					if (p.Body.ReadU32Be(4) == StatDamageCodec.Opcode)
					{
						StatDamageIIR cast;
						try
						{
							cast = StatDamageCodec.Parse(p.Body.AsSpan(4));
						}
						catch
						{
							continue;
						}

						// ⚠️ The record's "StatId" slot is the damage SCHOOL for a damage record, not a stat id - treating
						// it as one makes Rend reduce stat 1 rather than deal Slashing. So the magical branch IS
						// represented here.
						//
						// The old justification for that ("every observed value is 0..10, i.e. exactly the DamageSchool
						// range") was WRONG on both halves and is what #487 unpicked: the corpus also carries 12
						// (Environmental - fall damage) and 23 (Heal), and the 0..10 numbering itself had Cold in the
						// wrong slot. Fitting inside a range never identified the mapping.
						//
						// Non-damage rows drop out here rather than needing their own filter: Heal is excluded because
						// all 110 of its occurrences carry a non-negative delta, and Environmental because a fall has no
						// caster and its target is a player, so neither NPC branch below claims it.
						if (cast.Delta >= 0)
							continue;

						// Same both-ends rule as the melee tables below: a pet is a spawned entity too, so checking only
						// one end files the PET's own nukes as NPC casts, and a mob's DoT on the pet as damage to the player.
						var magnitude = (uint)(-cast.Delta);
						var casterIsNpc = levels.TryGetValue(cast.Caster, out var casterLevel);
						var spellTargetIsNpc = levels.TryGetValue(cast.Handle, out var spellTargetLevel);

						if (casterIsNpc && !spellTargetIsNpc)
						{
							fromNpcSpell.TryAdd((casterLevel, cast.School), []);
							fromNpcSpell[(casterLevel, cast.School)].Add(magnitude);
						}

						if (spellTargetIsNpc && !casterIsNpc)
						{
							toNpcSpell.TryAdd((spellTargetLevel, cast.School), []);
							toNpcSpell[(spellTargetLevel, cast.School)].Add(magnitude);
						}

						continue;
					}

					if (p.Body.ReadU32Be(4) != DamageResultCodec.Opcode)
						continue;

					DamageResultIIR hit;
					try
					{
						hit = DamageResultCodec.Parse(p.Body.AsSpan(4));
					}
					catch
					{
						continue; // a truncated/odd record - counted by its absence, not by a wrong number
					}

					if (hit.Damage == 0)
						continue;

					pendingHits[hit.Target] = hit;

					// ⚠️ BOTH directions need BOTH ends checked, not just one. `levels` holds every entity the capture
					// spawned - which includes the player's own PETS - so testing only the attacker counted a mob
					// hitting the necro's pet as "NPC -> PLAYER", and testing only the target counted the PET's hits
					// as "PLAYER -> NPC". It showed: `Parasitic Soul` (a necro summon) sat in the NPC->player table
					// as a level-80 attacker dealing 8556. An entity that is not a known spawn is the player.
					var attackerIsNpc = levels.TryGetValue(hit.Attacker, out var attackerLevel);
					var targetIsNpc = levels.TryGetValue(hit.Target, out var targetLevel);

					if (attackerIsNpc && !targetIsNpc)
					{
						fromNpc.TryAdd((attackerLevel, hit.School), []);
						fromNpc[(attackerLevel, hit.School)].Add(hit.Damage);

						// ...and the same hit kept ATTRIBUTED, so the archetype spread stays visible.
						// Keyed by TEMPLATE, never by NAME (#244): ~16 unrelated templates share the internal name
						// "Halen van Dummy", and an elite variant routinely carries the SAME display name as its
						// ordinary cousin - so a name key silently averages two different creatures together.
						if (templates.TryGetValue(hit.Attacker, out var attackerTemplate))
						{
							var key = (attackerTemplate, attackerLevel);
							fromNpcByAttacker.TryAdd(key, []);
							fromNpcByAttacker[key].Add(hit.Damage);
							if (spawnHealth.TryGetValue(hit.Attacker, out var attackerHp))
								attackerHealth[key] = attackerHp;
							if (names.TryGetValue(hit.Attacker, out var attackerName))
								attackerNames[attackerTemplate] = attackerName;
							// The level of the player this hit actually LANDED ON, by victim handle.
							if (playerLevels.TryGetValue(hit.Target, out var victimLevel))
								attackerPlayerLevel[key] = victimLevel;
						}
					}

					// The kill-scale check asks "did this NPC take its own health in damage before it died", so it
					// accumulates damage from ANY source - the player, their pet, a passing guard. Filtering it to
					// player-only hits would undercount every mob a pet helped kill and drag the ratio down.
					if (targetIsNpc)
						dealtTo[hit.Target] = dealtTo.GetValueOrDefault(hit.Target) + hit.Damage;

					if (targetIsNpc && !attackerIsNpc)
					{
						toNpc.TryAdd((targetLevel, hit.School), []);
						toNpc[(targetLevel, hit.School)].Add(hit.Damage);

						// ...and as a FRACTION of what that NPC actually had. A level bucket mixes a 63-health
						// chicken with a 63,000-health elite, so a median damage figure across it means nothing;
						// "what fraction of the target did this hit take" is comparable across both, and its
						// reciprocal is the number a designer actually cares about - hits to kill.
						if (spawnHealth.TryGetValue(hit.Target, out var targetHp) && targetHp > 0)
						{
							var pct = 100.0 * hit.Damage / targetHp;
							hitsToKill.TryAdd(targetLevel, []);
							hitsToKill[targetLevel].Add(pct);
						}
					}
				}

				// An NPC that died took at least its own health in damage; the RATIO of the two says whether the
				// damage number shares the health scale. Only NPCs whose total >= a third of their health are
				// counted, so a mob the player walked away from does not read as "the scale is 3x".
				foreach (var (handle, dealt) in dealtTo)
				{
					if (!spawnHealth.TryGetValue(handle, out var hp) || hp == 0 || dealt * 3 < hp)
						continue;

					var bucket = Math.Round((double)dealt / hp, 2);
					killRatios[bucket] = killRatios.GetValueOrDefault(bucket) + 1;
				}
			}

			Console.WriteLine($"\n=== SCALE: total damage an NPC took vs the health its SPAWN declared ===");
			Console.WriteLine($"{"ratio",8} {"npcs",6}   (~1 = same scale as health; ~0.01 = damage is x100 of health)");
			foreach (var (bucket, n) in killRatios.OrderByDescending(k => k.Value).Take(8))
				Console.WriteLine($"{bucket,8:F2} {n,6}");

			Console.WriteLine($"\n=== SCALE: does a hit move the target's health by the SAME number? ===");
			Console.WriteLine($"{"ratio",8} {"pairs",6}   (1.00 = damage and health share a scale; 0.01 = damage is x100 of health)");
			foreach (var (ratio, n) in scaleRatios.OrderByDescending(k => k.Value).Take(6))
				Console.WriteLine($"{ratio,8:F2} {n,6}");

			// The model is the SERVER's, called - not a copy of it. This column used to hardcode `5 + level * 3`, a
			// formula the server had already replaced with `level * 100`, so it spent that time reporting our fit
			// against a curve nothing ran (#479).
			// `level, level` = a player at the mob's own level, i.e. the un-penalised case - the level-gap penalty
			// (#488) is a separate factor and the column below is the one that shows it.
			// ⚠️ WIRE units, not the resolver's display return. Captured damage is a wire number, and comparing it
			// to the display value multiplied every `x model` in this report by 100 from #572 until #671 caught it.
			Report("NPC -> PLAYER (what ResolveNpcAttackDamage should reproduce)", fromNpc, level => CombatResolver.NpcAttackDamageWire(level, level));

			// Per attacker, so the archetype spread is visible instead of averaged away. `x model` is the measured
			// median over what the server would deal at that level: ~1.0 means the level curve fits this attacker.
			Console.WriteLine($"\n=== NPC -> PLAYER, BY ATTACKER (the level buckets above mix archetypes) ===");
			Console.WriteLine("⚠️ `x model` is scored in WIRE units on both sides. `modes` splits an attacker's hits at their");
			Console.WriteLine("   largest RATIO gap when that gap is >= 3x with 2+ hits either side - a median taken across two");
			Console.WriteLine("   mechanics (an auto-swing and a special) describes neither, which is what #671 stalled on.");
			Console.WriteLine($"{"level",5} {"tpl",8} {"attacker",-24} {"hits",5} {"min",7} {"median",7} {"max",7} {"ours",7} {"x model",8} {"pc lvl",7} {"lvl gap",8} {"hp/curve",9} {"hit%hp",7}  modes");
			foreach (var ((template, level), hits) in fromNpcByAttacker.OrderBy(k => k.Key.Level).ThenBy(k => k.Key.Template))
			{
				var sorted = hits.Order().ToList();
				var median = sorted[sorted.Count / 2];
				var pcLvl = attackerPlayerLevel.GetValueOrDefault((template, level));
				// Scored against what the server would ACTUALLY deal to that player - the level-gap penalty included
				// (#488) - so `x model` reads 1.0 when we reproduce the fight rather than only when the gap is 0. Rows
				// with no resolvable player level fall back to the un-penalised curve.
				var ours = CombatResolver.NpcAttackDamageWire(level, pcLvl > 0 ? pcLvl : level);

				var hp = attackerHealth.GetValueOrDefault((template, level));
				var levelHp = healthByLevel.TryGetValue(level, out var pool) && pool.Count > 0 ? pool.Order().ToList()[pool.Count / 2] : 0;
				var hpRatio = hp > 0 && levelHp > 0 ? (double)hp / levelHp : 0;

				Console.WriteLine($"{level,5} {template,8} {Trunc(attackerNames.GetValueOrDefault(template, "?"), 24),-24} {sorted.Count,5} {sorted[0],7} {median,7} {sorted[^1],7} {ours,7} {(ours > 0 ? (double)median / ours : 0),8:F2} {(pcLvl > 0 ? pcLvl.ToString() : "-"),7} {(pcLvl > 0 ? ((int)pcLvl - (int)level).ToString() : "-"),8} {(hpRatio > 0 ? hpRatio.ToString("F1") : "-"),9} {(hp > 0 ? (100.0 * median / hp).ToString("F2") : "-"),7}  {Modes(sorted, ours)}");
			}

			// ⭐ THE HEAD-TO-HEAD (#671). The ticket asked for an ELITE multiplier on top of the level curve. The
			// better question is whether LEVEL is the right base at all: we also hold the attacker's own maximum
			// health (`NpcSpawn.Health`), and a swing that is a fixed share of the swinger's pool would explain the
			// "elites hit harder" observation without an archetype column, because an elite's pool is what makes it
			// an elite.
			//
			// Scored only where the model is not being asked to do the level-gap's job too - |gap| <= 2 - and only
			// on rows with enough hits to have a median worth the name. The statistic is the SPREAD: a predictor
			// whose ratio clusters is the one that predicts.
			var comparable = fromNpcByAttacker
				.Where(kv => kv.Value.Count >= 8 && attackerHealth.ContainsKey(kv.Key) && attackerPlayerLevel.TryGetValue(kv.Key, out var pc) && Math.Abs((int)pc - (int)kv.Key.Level) <= 2)
				.Select(kv =>
				{
					var sorted = kv.Value.Order().ToList();
					var median = sorted[sorted.Count / 2];
					var model = CombatResolver.NpcAttackDamageWire(kv.Key.Level, attackerPlayerLevel[kv.Key]);
					return (Name: attackerNames.GetValueOrDefault(kv.Key.Template, "?"), kv.Key.Level, sorted.Count,
						ByLevel: model > 0 ? (double)median / model : 0,
						ByHealth: 100.0 * median / attackerHealth[kv.Key]);
				})
				.OrderBy(r => r.Level)
				.ToList();

			Console.WriteLine();
			Console.WriteLine($"=== NPC -> PLAYER: is LEVEL or the attacker's own HEALTH the better base? ({comparable.Count} rows, |gap| <= 2, >= 8 hits) ===");
			Console.WriteLine($"{"level",5} {"attacker",-26} {"hits",5} {"x level model",14} {"% of own hp",12}");
			foreach (var r in comparable)
				Console.WriteLine($"{r.Level,5} {Trunc(r.Name, 26),-26} {r.Count,5} {r.ByLevel,14:F2} {r.ByHealth,12:F2}");

			if (comparable.Count >= 3)
			{
				Console.WriteLine($"  by LEVEL : min {comparable.Min(r => r.ByLevel):F2} max {comparable.Max(r => r.ByLevel):F2}"
				                  + $" -> spread {comparable.Max(r => r.ByLevel) / Math.Max(0.0001, comparable.Min(r => r.ByLevel)):F1}x");
				Console.WriteLine($"  by HEALTH: min {comparable.Min(r => r.ByHealth):F2}% max {comparable.Max(r => r.ByHealth):F2}%"
				                  + $" -> spread {comparable.Max(r => r.ByHealth) / Math.Max(0.0001, comparable.Min(r => r.ByHealth)):F1}x"
				                  + $", median {comparable.Select(r => r.ByHealth).Order().ToList()[comparable.Count / 2]:F2}%");
			}

			static string Trunc(string s, int n)
			{
				return s.Length <= n ? s : s[..n];
			}

			// Splits an attacker's hits at the largest RATIO gap between consecutive sorted values, when that gap is
			// wide enough and both sides are populated. A per-archetype damage multiplier cannot express two
			// mechanics in one number, so before fitting one the question is whether there IS one number: `Vanir
			// Sentinel` L26's 25 hits run 127 / 4191 / 5217, where 127 is what the level-gap model predicts and 5217
			// is 40x that. One template, one level, one player, one fight - and two mechanics.
			static string Modes(List<uint> sorted, uint model)
			{
				const double splitRatio = 3.0;
				const int minSide = 2;
				if (sorted.Count < 2 * minSide)
					return "-";

				var at = -1;
				var best = splitRatio;
				for (var i = minSide - 1; i <= sorted.Count - minSide - 1; i++)
				{
					if (sorted[i] == 0)
						continue;

					var ratio = (double)sorted[i + 1] / sorted[i];
					if (ratio <= best)
						continue;

					best = ratio;
					at = i;
				}

				if (at < 0)
					return "one";

				var low = sorted[..(at + 1)];
				var high = sorted[(at + 1)..];
				return $"TWO: {low.Count}x median {low[low.Count / 2]} ({Times(low[low.Count / 2], model)})"
				       + $" | {high.Count}x median {high[high.Count / 2]} ({Times(high[high.Count / 2], model)}), {best:F1}x apart";
			}

			static string Times(uint value, uint model)
			{
				return model > 0 ? $"{(double)value / model:F2}x" : "-";
			}
			Report("PLAYER -> NPC (the shape an NPC-armour model has to reproduce)", toNpc, null);
			Console.WriteLine($"\n=== PLAYER -> NPC as a FRACTION of the target's own health ===");
			Console.WriteLine($"{"level",5} {"hits",6} {"median %",9} {"hits-to-kill",13}");
			foreach (var (level, pcts) in hitsToKill.OrderBy(k => k.Key))
			{
				var sorted = pcts.Order().ToList();
				var median = sorted[sorted.Count / 2];
				Console.WriteLine($"{level,5} {sorted.Count,6} {median,9:F1} {(median > 0 ? 100.0 / median : 0),13:F1}");
			}

			Report("NPC -> PLAYER, SPELL/DoT damage (StatDamageIIR)", fromNpcSpell, null);
			Report("PLAYER -> NPC, SPELL/DoT damage (StatDamageIIR)", toNpcSpell, null);

			// The school decides which mitigation BRANCH the client runs (FUN_0113ba00 switches on it): 1/2/3/8 take
			// the physical branch - armour, Strength-scaled - and everything else the magical one, protection scaled
			// by Int/Wis. Mixing them into one average would compare a sword swing with a fire spell.
			static void Report(string title, Dictionary<(uint Level, DamageSchool School), List<uint>> byLevel, Func<uint, uint>? model)
			{
				Console.WriteLine($"\n=== {title} ===");
				Console.WriteLine($"{"level",5} {"school",-12} {"branch",-9} {"hits",6} {"min",6} {"median",7} {"mean",7} {"max",6}" + (model == null ? "" : $" {"ours",6}"));
				foreach (var ((level, school), hits) in byLevel.OrderBy(k => k.Key.Level).ThenBy(k => k.Key.School))
				{
					var sorted = hits.Order().ToList();
					var branch = school is DamageSchool.Slashing or DamageSchool.Piercing or DamageSchool.Crushing or DamageSchool.Poison
						? "physical"
						: "magical";
					var line = $"{level,5} {school,-12} {branch,-9} {sorted.Count,6} {sorted[0],6} {sorted[sorted.Count / 2],7} {sorted.Select(v => (double)v).Average(),7:F1} {sorted[^1],6}";
					Console.WriteLine(model == null ? line : line + $" {model(level),6}");
				}
			}
		}

		// Does an NPC spawn carry any per-NPC STAT array? (#401's player->NPC half needs an armour source, and the
		// stat-update IIR carries none - tools/capture/scan_npc_armor_stat.py.) The spawn's character message has a
		// `generic_stats` repeated-uint32 field; this censuses it over every captured NPC spawn using the real
		// protobuf decoder, because hand-scanning the wire for the field tag matches inside strings.
		// --report spawn-diff [--report-arg <name substring>]
		//
		// Rebuilds every CAPTURED NPC spawn from the DB row it was imported into and compares the two - the question
		// "does the NPC we send look like the NPC retail sent", asked of the shipped data rather than of a golden
		// fixture. That is what an in-client "this NPC renders wrong" report needs, and what the equipment-row and
		// stat-core queries cannot answer on their own: both can be faithful while the record assembled from them is
		// not.
		//
		// It compares the two parts that DESCRIBE the entity and that we claim to reproduce - the embedded
		// CoreCharacter message (identity, stats, worn equipment) and the post-IIR appearance/morph table - plus the
		// CoreChar header word. It deliberately does NOT compare whole records: our pre-IIR is the generated minimal
		// scaffold by design (#243), so a byte-exact record comparison reports every spawn in the corpus as different
		// and says nothing about appearance.
		//
		// The match is exact: a captured record is found by the row's OWN derived key (map + position), over the login
		// burst only - the window the importer imports through, since a mid-session spawn is the same NPC at wherever
		// it had WANDERED to and keys to no row. A row we cannot find, or a type with no lifted stat core, is reported
		// rather than skipped: "not compared" is a different answer from "matches".
		//
		// ⚠️ NOROW is not by itself a data gap. The key is built with a NULL class, so every placement on a
		// CLASS-SPECIFIC map (the city armories, where the class is part of the identity) keys to nothing here - the
		// quartermasters are all NOROW for that reason alone. Read a NOROW as "go look", not as "missing".
		private static void SpawnDiff(IReadOnlyList<NamedCapture> captures, string? filter)
		{
			// The client's spawn poll, which ends the login-burst window (NpcSpawnExtractor names it the same way).

			using var db = OpenDb();
			var rows = db
				.NpcSpawns.AsNoTracking()
				.Include(s => s.NpcTemplate).ThenInclude(t => t.StatCore)
				.Include(s => s.Equipment).ThenInclude(e => e.Item)
				.Include(s => s.Morphs)
				.AsSplitQuery()
				.ToDictionary(s => s.Id);

			// Per template: how the captured records for it compared. Keyed by NAME because that is what an in-client
			// report names, and a name can span several template ids (two "Poacher" types stand on one map).
			var tally = new SortedDictionary<string, SpawnVerdict>();
			var seen = new HashSet<(uint, float, float, float)>();
			var word2Tables = 0;
			var word2NonZero = 0;
			var word2Values = new Dictionary<uint, int>();
			var word2Prefix = new Dictionary<string, int>();
			var word2Carriers = new HashSet<(uint Map, float X, float Y, float Z)>();

			foreach (var capture in captures)
			{
				uint map = 0;
				var sawPoll = false;
				foreach (var p in capture.Packets)
				{
					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
					{
						if (p.TryParse(PlayfieldFCodec.Parse, out var change))
							map = change.MapId;

						sawPoll = false;
						continue;
					}

					// The LOGIN BURST window only, the same gate NpcSpawnExtractor.ExtractLoginBurst imports through
					// (the client's spawn poll ends it).
					if (!p.IsDownload && p.WireOpcode == NpcSpawnExtractor.QuestMarkerQueryWireOpcode)
					{
						sawPoll = true;
						continue;
					}

					if (sawPoll || map == 0 || p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					SimpleCharFullUpdateIIR captured;
					try
					{
						captured = SimpleCharFullUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue; // a body the codec cannot account for is not one we build; #243's mid-session forms
					}

					// Neither the player's own spawn nor another player's is an NpcSpawn row.
					if (captured.Handle >= EntityHandles.CharacterInstanceBase || captured.Character?.player_stats != null)
						continue;

					// ⚠️ The Word2 census runs HERE - before the name filter, the position dedup and the DB-row
					// lookup - because it is a claim about retail's BYTES, not about the spawns we can compare. It
					// used to sit at the bottom of the loop, past all three gates, and reported "2 of 756 tables"
					// while the importer's own lift found a THIRD carrying spawn (Valkyrie Skalla) that the dedup had
					// dropped. A census that silently answers a narrower question than it states is the failure mode
					// this whole field's history is made of.
					if (captured.Post?.Slot is NpcEntrySlot { Morphs.Count: > 0 } census)
					{
						word2Tables++;
						var nonZero = census.Morphs.FindAll(m => m.Word2 != 0);
						if (nonZero.Count > 0)
						{
							word2NonZero++;
							word2Carriers.Add((map, captured.Position.X, captured.Position.Y, captured.Position.Z));
							foreach (var m in nonZero)
								word2Values[m.Word2] = word2Values.GetValueOrDefault(m.Word2) + 1;

							// "the first N entries and no others" is the shape an ordered appearance field has and
							// scattered runtime state does not.
							var prefix = census.Morphs.FindIndex(m => m.Word2 == 0);
							var contiguous = prefix >= 0 && !census.Morphs.Skip(prefix).Any(m => m.Word2 != 0);
							var key = contiguous ? $"{prefix} entries" : "scattered";
							word2Prefix[key] = word2Prefix.GetValueOrDefault(key) + 1;
						}
					}

					var name = captured.Character?.Name ?? "";
					if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
						continue;

					// One verdict per PHYSICAL spawn, not per captured send: the same NPC is re-sent on every login in
					// every capture, and counting those would weight the tally by how often a zone was visited.
					var pos = captured.Position;
					if (!seen.Add((map, pos.X, pos.Y, pos.Z)))
						continue;

					var v = tally.GetValueOrDefault(name) ?? (tally[name] = new SpawnVerdict());
					if (!rows.TryGetValue(NpcSpawn.ComputeId(map, pos.X, pos.Y, pos.Z, null), out var row))
					{
						v.NoRow++;
						continue;
					}

					SimpleCharFullUpdateIIR ours;
					try
					{
						ours = SimpleCharFullUpdateCodec.Parse(NpcSpawnBuilder.BuildLive(row, captured.Handle, (uint)captured.Playfield));
					}
					catch (Exception e) when (e is InvalidOperationException or CodecParseException)
					{
						v.Failed++;
						v.Note ??= e.Message;
						continue;
					}

					var faults = new List<string>();
					if (captured.CoreCharIirHeader != ours.CoreCharIirHeader)
						faults.Add($"iir header 0x{captured.CoreCharIirHeader:X} vs 0x{ours.CoreCharIirHeader:X}");

					var theirMsg = captured.Character?.ToProtobuf() ?? [];
					var ourMsg = ours.Character?.ToProtobuf() ?? [];
					if (!theirMsg.AsSpan().SequenceEqual(ourMsg))
					{
						// WHICH fields, not just "the message differs". Named fields are what turn a diff into a lead:
						// "we send MonsterScale 100 where retail sends 97" is actionable, "758B vs 758B" is not.
						var fields = FieldDiff(captured.Character, ours.Character);
						faults.Add($"message {theirMsg.Length}B vs {ourMsg.Length}B"
							+ $" (equip {captured.Character?.InventoryEntries.Count ?? 0} vs {ours.Character?.InventoryEntries.Count ?? 0})"
							+ (fields.Count > 0 ? $" [{string.Join(", ", fields.Take(6))}{(fields.Count > 6 ? ", ..." : "")}]" : ""));
					}

					var theirMorphs = Morphs(captured);
					var ourMorphs = Morphs(ours);
					if (!theirMorphs.SequenceEqual(ourMorphs))
						faults.Add($"appearance {theirMorphs.Count} morphs vs {ourMorphs.Count}"
							+ (theirMorphs.Count == ourMorphs.Count
								? " (same count, different values)"
								: theirMorphs.Count > 0 && ourMorphs.Count == 0
									? $" - BALD, declined because {AppearanceDeclineReason(captured)}"
									: ""));
					else if (captured.Post?.MovementMode != ours.Post?.MovementMode)
						faults.Add($"post-IIR movement mode {captured.Post?.MovementMode} vs {ours.Post?.MovementMode}");

					if (faults.Count == 0)
					{
						v.Same++;
						continue;
					}

					v.Differ++;
					v.Note ??= string.Join("; ", faults);
				}
			}

			// #672: the classifier declines a whole appearance table on ONE entry with a non-zero Word2, and every
			// bald NPC found so far declines for exactly that. So census the field across every captured table: if
			// it were runtime noise it would be scattered, and if it is part of the appearance it will have shape.
			Console.WriteLine();
			Console.WriteLine("appearance Word2 census (the field that USED to decline a table - #672):");
			Console.WriteLine($"  captured sends with a morph list: {word2Tables}, of which {word2NonZero} carry a non-zero Word2 on some entry"
			                  + $" ({word2Carriers.Count} distinct placement(s))");
			Console.WriteLine($"  Word2 values: {string.Join(", ", word2Values.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} x{kv.Value}"))}");
			Console.WriteLine($"  and on a carrying table, the non-zero entries are its FIRST {string.Join("/", word2Prefix.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} ({kv.Value})"))}");

			// ⚠️ Cross-check against what the IMPORTER lifted, because this report can only see the LOGIN BURST and
			// the importer sees more. That gap is not hypothetical: the burst window shows two carrying placements
			// (Turach, Kincaid) and the DB holds three - `Valkyrie Skalla` carries one from a send this window never
			// reaches. Reporting only the burst figure would state a floor as if it were the count, which is exactly
			// the error #672 was opened about.
			var carriedInDb = rows.Values.Where(r => r.Morphs.Any(m => m.Word2 != 0)).Select(r => r.NpcTemplate?.Name ?? $"#{r.NpcTemplateId}").Order().ToList();
			Console.WriteLine($"  the DB (which the importer lifts from a WIDER window than this report sees) holds "
			                  + $"{carriedInDb.Count} carrying placement(s): {string.Join(", ", carriedInDb)}");

			// ⭐ WHAT it is attached to. The ordinal shape alone says "not runtime state"; this says what it IS.
			// Resolving each carrying morph id against MorphTargets: every one is a `*texscript_*`, never a face/body
			// slider or a hair mesh. And no single-texscript table carries it, which is what an ORDER needs two of -
			// though most multi-texscript tables do not carry it either, so it is not simply "the layer order".
			var morphNames = db.MorphTargets.AsNoTracking().ToDictionary(t => t.Id, t => t.Name);
			var carryingKinds = rows.Values
				.SelectMany(r => r.Morphs.Where(m => m.Word2 != 0))
				.GroupBy(m => morphNames.GetValueOrDefault(m.MorphId, "?").StartsWith('*') ? morphNames[m.MorphId].Split('_')[0] : "a plain morph slider")
				.ToDictionary(g => g.Key, g => g.Count());
			var texscriptRuns = rows.Values
				.Select(r => r.Morphs.Count(m => morphNames.GetValueOrDefault(m.MorphId, "").StartsWith("*texscript", StringComparison.Ordinal)))
				.Where(n => n > 0)
				.GroupBy(n => n)
				.ToDictionary(g => g.Key, g => g.Count());
			Console.WriteLine($"  and the carrying ENTRIES are: {string.Join(", ", carryingKinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"))}");
			Console.WriteLine($"  tables by texscript count: {string.Join(", ", texscriptRuns.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}->{kv.Value}"))}"
			                  + $" (single-texscript tables carrying the ordinal: {rows.Values.Count(r => r.Morphs.Count(m => morphNames.GetValueOrDefault(m.MorphId, "").StartsWith("*texscript", StringComparison.Ordinal)) == 1 && r.Morphs.Any(m => m.Word2 != 0))})");
			Console.WriteLine();

			Console.WriteLine("spawn-diff: every captured NPC spawn rebuilt from its DB row - CoreCharacter message +");
			Console.WriteLine("appearance table compared (the pre-IIR is our generated scaffold by design, so it is not).");
			Console.WriteLine("DIFFER = an NPC we describe differently from retail. NOROW/FAILED were not compared at all.");
			Console.WriteLine();
			Console.WriteLine($"{"npc",-34} {"same",6} {"differ",7} {"norow",6} {"failed",7}");
			foreach (var (name, v) in tally.OrderByDescending(x => x.Value.Differ + x.Value.Failed).ThenBy(x => x.Key))
			{
				Console.WriteLine($"{(name.Length > 33 ? name[..33] : name),-34} {v.Same,6} {v.Differ,7} {v.NoRow,6} {v.Failed,7}");
				if (v.Note != null)
					Console.WriteLine($"    {v.Note}");
			}

			Console.WriteLine();
			Console.WriteLine($"totals: {tally.Sum(t => t.Value.Same)} match, {tally.Sum(t => t.Value.Differ)} differ, "
				+ $"{tally.Sum(t => t.Value.NoRow)} with no DB row, {tally.Sum(t => t.Value.Failed)} unbuildable, over {tally.Count} names");

			return;

			// The appearance table as (morph id, value) pairs. Empty for an animal, whose post-IIR carries no entry
			// slot of this shape - so "0 vs 0" is a match, not two absences papering over each other.
			// #672: WHY a captured appearance table did not become morph rows. `NpcSpawnAppearance.TryLiftLenient` is
			// deliberately narrower than the reader it runs on and declines the whole table on any of four
			// conditions; a spawn that lands here renders BALD, so which condition fired is the whole question. It
			// re-states the classifier's conditions rather than calling it, because the classifier answers bool - and
			// the two are gated together by NpcSpawnAppearanceTests, so they cannot drift apart silently.
			static string AppearanceDeclineReason(SimpleCharFullUpdateIIR captured)
			{
				var post = captured.Post;
				if (post == null)
					return "the post-IIR did not decode";
				if (post.Slot is not NpcEntrySlot npc)
					return $"the entry slot is {post.Slot?.GetType().Name ?? "absent"}, not an NPC slot";
				if (npc.Morphs.Count == 0)
					return "the table is empty (the minimal no-morph form)";

				// Word2 came OFF this veto with #672 - it is an ordinal, not runtime state, and the column now carries
				// it - so only the never-observed Flag still declines a table.
				var flagged = npc.Morphs.FindIndex(m => m.Flag != 0);
				if (flagged >= 0)
					return $"entry {flagged} sets the never-observed Flag ({npc.Morphs[flagged].Flag})";
				if (npc.EntryTableX != ListFrame.Empty || npc.EntryTableMarker != ListFrame.Empty)
					return $"the entry table is not empty (X=0x{npc.EntryTableX:X}, marker=0x{npc.EntryTableMarker:X})";
				if (post.EndPairs.Count > 0 || post.EndPairs2.Count > 0 || post.EndPairs3.Count > 0)
					return $"a closing list is not empty ({post.EndPairs.Count}/{post.EndPairs2.Count}/{post.EndPairs3.Count} pairs)";

				return "no classifier condition fires - the rows are missing for another reason (a stale import?)";
			}

			static List<(uint, float)> Morphs(SimpleCharFullUpdateIIR spawn)
			{
				return spawn.Post?.Slot is NpcEntrySlot npc
					? npc.Morphs.Select(m => (m.MorphId, m.Value)).ToList()
					: [];
			}
		}

		// Names the SCALAR fields two protobuf messages disagree on, recursing into sub-messages. Reflective rather
		// than a hand-written field list because the message is generated: a field added to the schema has to show up
		// here without anyone remembering to add it, or the diff quietly under-reports.
		//
		// Repeated fields report only their COUNT - element-wise diffing them is the caller's job when it matters
		// (worn equipment already has its own line) - and a null-vs-set sub-message is named rather than walked.
		// --report field-stability [--report-arg <name substring>]
		//
		// For every field of the captured NPC CoreCharacter message: is its value a property of the NPC TYPE, or of
		// the individual placement? Asked of retail's own bytes, with no DB involved.
		//
		// This is the question `--report spawn-diff` raises but cannot answer (Gitea #602). A spawn-diff difference
		// says our value disagrees with retail's; it does not say whether the disagreement is a bad lift or a field
		// that has no type-level value to lift. `NpcStatCoreColumns.Lift` is faithful to the message it is handed -
		// the fault, where there is one, is that a per-PLACEMENT field was lifted to the TYPE at all, and then every
		// placement of that type inherits whichever value the representative spawn happened to carry.
		//
		// The test is a GROUP BY, not a threshold: group the captured spawns by TYPE, keep the types sighted more
		// than once, and count in how many of them the field holds one distinct value. A type-stable field is
		// constant in ~all of them; a per-placement field is constant only where the type happens to have been
		// sighted in one state. Reported as that ratio plus the worst offender, so "83% stable" is readable as
		// "nearly type-stable, with a real exception to look at" rather than as a pass mark.
		//
		// ⚠️ The group key is retail's own `npc_stats.TemplateId`, NOT the name. Grouping by name was the first cut
		// and it is wrong: a name spans several templates (the same report scored `TemplateId` itself at 70% stable
		// under a name key - "Halen van Dummy" alone covers 22 of them), so every field inherits that conflation and
		// reads as more placement-varying than it is. A field cannot be less stable than the key it is grouped by.
		//
		// ⚠️ Only types sighted MORE THAN ONCE can say anything: a type seen once is trivially "constant" and would
		// inflate every field toward 100%. The single-sighting types are counted and reported separately.
		private static void FieldStability(IReadOnlyList<NamedCapture> captures, string? filter)
		{

			// field name -> npc TEMPLATE -> the distinct values seen for it
			var fields = new SortedDictionary<string, Dictionary<uint, HashSet<string>>>(StringComparer.Ordinal);
			var sightings = new Dictionary<uint, int>();
			var templateNames = new Dictionary<uint, string>();

			// ...and the same thing keyed by PHYSICAL PLACEMENT (map + position), which is the second half of the
			// question and the one that decides whether a per-placement field can live in a table AT ALL. A field
			// that varies between sightings of one placement is not placement-scoped either - it is rolled per spawn
			// EVENT, and no column can hold it. This accumulator deliberately does NOT dedup by position: repeated
			// sightings of one placement are exactly its evidence.
			var byPlacement = new SortedDictionary<string, Dictionary<(uint, float, float, float), HashSet<string>>>(StringComparer.Ordinal);
			var placementSightings = new Dictionary<(uint, float, float, float), int>();
			var placementNames = new Dictionary<(uint, float, float, float), string>();
			var seen = new HashSet<(uint, float, float, float)>();

			foreach (var capture in captures)
			{
				var sawPoll = false;
				uint map = 0;
				foreach (var p in capture.Packets)
				{
					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
					{
						if (p.TryParse(PlayfieldFCodec.Parse, out var change))
							map = change.MapId;

						sawPoll = false;
						continue;
					}

					if (!p.IsDownload && p.WireOpcode == NpcSpawnExtractor.QuestMarkerQueryWireOpcode)
					{
						sawPoll = true;
						continue;
					}

					if (sawPoll || map == 0 || p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					SimpleCharFullUpdateIIR captured;
					try
					{
						captured = SimpleCharFullUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue;
					}

					if (captured.Handle >= EntityHandles.CharacterInstanceBase || captured.Character?.player_stats != null)
						continue;

					var name = captured.Character?.Name ?? "";
					if (name.Length == 0 || (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
						continue;

					// The TYPE, from retail's own message - see the group-key warning above.
					var template = captured.Character?.npc_stats?.TemplateId ?? 0;
					if (template == 0)
						continue;

					var pos = captured.Position;
					var placement = (map, pos.X, pos.Y, pos.Z);
					var values = FieldValues(captured.Character);

					placementSightings[placement] = placementSightings.GetValueOrDefault(placement) + 1;
					placementNames[placement] = name;
					foreach (var (field, value) in values)
					{
						var perPlacement = byPlacement.TryGetValue(field, out var pd) ? pd : byPlacement[field] = new Dictionary<(uint, float, float, float), HashSet<string>>();
						(perPlacement.TryGetValue(placement, out var pv) ? pv : perPlacement[placement] = []).Add(value);
					}

					// One reading per PHYSICAL spawn for the TYPE table, the same dedup spawn-diff uses: the same NPC
					// is re-sent on every login in every capture, and counting those would score a field by how often
					// a zone was visited.
					if (!seen.Add(placement))
						continue;

					sightings[template] = sightings.GetValueOrDefault(template) + 1;
					templateNames[template] = name;
					foreach (var (field, value) in values)
					{
						var perType = fields.TryGetValue(field, out var d) ? d : fields[field] = new Dictionary<uint, HashSet<string>>();
						(perType.TryGetValue(template, out var vals) ? vals : perType[template] = []).Add(value);
					}
				}
			}

			var repeated = sightings.Where(s => s.Value > 1).Select(s => s.Key).ToHashSet();
			Console.WriteLine("field-stability: is a captured NPC field a property of the TYPE or of the PLACEMENT?");
			Console.WriteLine("Measured over retail's own spawns - no DB row is read, so this says nothing about our lift,");
			Console.WriteLine("only about whether there is a type-level value TO lift (Gitea #602).");
			Console.WriteLine();
			Console.WriteLine($"{sightings.Count} templates, {repeated.Count} of them sighted more than once "
				+ $"({sightings.Count - repeated.Count} single-sighting templates excluded - they are trivially constant).");
			Console.WriteLine();
			Console.WriteLine($"{"field",-40} {"stable",7} {"of",5}  {"%",6}  worst offender");

			var scored = new List<(string Field, int Stable, int Total, double Pct, string Worst)>();
			foreach (var (field, perName) in fields)
			{
				var considered = perName.Where(k => repeated.Contains(k.Key)).ToList();
				if (considered.Count == 0)
					continue;

				var stable = considered.Count(k => k.Value.Count == 1);
				var worst = considered.OrderByDescending(k => k.Value.Count).First();
				scored.Add((field, stable, considered.Count, 100.0 * stable / considered.Count,
					worst.Value.Count == 1 ? "" : $"{templateNames[worst.Key]} #{worst.Key} ({worst.Value.Count} values: {string.Join("/", worst.Value.Take(4))})"));
			}

			foreach (var s in scored.OrderBy(s => s.Pct).ThenBy(s => s.Field))
				Console.WriteLine($"{(s.Field.Length > 39 ? s.Field[..39] : s.Field),-40} {s.Stable,7} {s.Total,5}  {s.Pct,5:F1}%  {s.Worst}");

			// Second table: the SAME physical NPC, sighted on more than one login. A field unstable here cannot be
			// stored at all - not on the type, not on the placement.
			var repeatedPlacements = placementSightings.Where(k => k.Value > 1).Select(k => k.Key).ToHashSet();
			Console.WriteLine();
			Console.WriteLine("--- by PHYSICAL PLACEMENT (map + position), the same NPC seen on more than one login ---");
			Console.WriteLine("A field unstable HERE is rolled per spawn event and belongs in no table, type or placement.");
			Console.WriteLine();
			Console.WriteLine($"{repeatedPlacements.Count} placements sighted more than once, of {placementSightings.Count}.");
			Console.WriteLine();
			Console.WriteLine($"{"field",-40} {"stable",7} {"of",5}  {"%",6}  worst offender");

			var placementScored = new List<(string Field, int Stable, int Total, double Pct, string Worst)>();
			foreach (var (field, perPlacement) in byPlacement)
			{
				var considered = perPlacement.Where(k => repeatedPlacements.Contains(k.Key)).ToList();
				if (considered.Count == 0)
					continue;

				var stable = considered.Count(k => k.Value.Count == 1);
				var worst = considered.OrderByDescending(k => k.Value.Count).First();
				placementScored.Add((field, stable, considered.Count, 100.0 * stable / considered.Count,
					worst.Value.Count == 1 ? "" : $"{placementNames[worst.Key]} ({worst.Value.Count} values: {string.Join("/", worst.Value.Take(4))})"));
			}

			foreach (var s in placementScored.OrderBy(s => s.Pct).ThenBy(s => s.Field).Take(25))
				Console.WriteLine($"{(s.Field.Length > 39 ? s.Field[..39] : s.Field),-40} {s.Stable,7} {s.Total,5}  {s.Pct,5:F1}%  {s.Worst}");

			// Third table: the VALUE DISTRIBUTION of the fields the two tables above condemn. Knowing a field is
			// rolled per spawn event says a column cannot hold it; it does not say what WE should send instead. For a
			// clock the expected answer is 0 - but that is a measurement, not an assumption (Gitea #602 item 2), and
			// this is the measurement: if the field is an animation PHASE, retail's own values are spread across the
			// whole range and 0 is simply one of them, so sending 0 is inside retail's distribution rather than a
			// value retail never sends.
			var placementPct = placementScored.ToDictionary(s => s.Field, s => s.Pct);
			// THREE conditions, and dropping any one of them fills the table with fields that belong in neither of the
			// two above. `placement <= type + 2pp` is the per-spawn-event test proper; `type < 100` requires the field
			// to BE unstable before asking at which scope; and `placement < 100` is what separates a field that varies
			// WITHIN one placement from one that is perfectly placement-stable and merely varies between placements
			// (`PetOwnerInstance` is 98.2% / 100.0% - the 2pp band admits it, and it is placement-scoped, not rolled).
			var perEvent = scored
				.Where(s => s.Pct < 100.0
					&& placementPct.TryGetValue(s.Field, out var p)
					&& p < 100.0
					&& p <= s.Pct + 2.0)
				.OrderBy(s => s.Pct)
				.ToList();

			Console.WriteLine();
			Console.WriteLine("--- value distribution of the per-SPAWN-EVENT fields ---");
			Console.WriteLine("A field no more stable at PLACEMENT scope than at TYPE scope (within 2pp) is rolled per spawn");
			Console.WriteLine("event. What follows is what retail actually sends for each, so the value WE send can be chosen");
			Console.WriteLine("from inside retail's own range rather than guessed.");
			foreach (var s in perEvent)
			{
				var values = fields[s.Field].SelectMany(v => v.Value).ToList();
				var distinct = values.Distinct(StringComparer.Ordinal).ToList();
				var top = values.GroupBy(v => v, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Take(6).ToList();
				Console.WriteLine();
				Console.WriteLine($"{s.Field} - type {s.Pct:F1}% / placement {placementPct[s.Field]:F1}%, {distinct.Count} distinct values");
				Console.WriteLine($"  most common: {string.Join(", ", top.Select(g => $"{g.Key} (x{g.Count()})"))}");

				// Continuous or enumerated? A clock reads as hundreds of distinct floats with no value dominating; an
				// enum reads as a handful. That difference is what decides whether 0 is "the start of a cycle" or a
				// state the client would have to interpret.
				// ⚠️ TWO traps here, and the second one cost a wrong conclusion once already.
				//
				// (1) "parses as a float" is NOT the clock test - every integer id parses as a float, which reads
				//     `npc_stats.TemplateId` (560 distinct ids) as a continuous phase.
				// (2) Counting DISTINCT values is not the clock test either. `AnimIdleTimer` has 94 distinct values
				//     of which 92 lie strictly between 0 and 1, which reads as an obviously-continuous phase - but
				//     those 92 are seen 1-13 times each while the two ENDPOINTS are seen 264 and 269 times. Weighted
				//     by how often retail actually sends them, the field is ~90% bimodal 0/1 with a rare transient,
				//     not a uniformly-sampled clock. A real clock would be roughly uniform across the range.
				//
				// So the test is the FREQUENCY share of the strictly-interior values, never their distinct count.
				var interior = values.Count(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 0 and < 1);
				var interiorDistinct = distinct.Count(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 0 and < 1);
				if (interiorDistinct > distinct.Count / 2)
				{
					var share = 100.0 * interior / values.Count;
					Console.WriteLine($"  {interiorDistinct} of {distinct.Count} distinct values lie strictly between 0 and 1, but they carry only {share:F0}% of");
					Console.WriteLine($"  sightings: 0 is sent {values.Count(v => v == "0")}x and 1 {values.Count(v => v == "1")}x, of {values.Count}.");
					Console.WriteLine(share > 50
						? "  CLOCK: the interior dominates, so this is a uniformly-sampled phase and any value is as good as another."
						: "  BIMODAL + TRANSIENT: the field RESTS at 0 or 1 and is only caught in between mid-blend. Which endpoint"
						+ "\n  it rests at is real information - do NOT flatten it to one value.");
				}
				else
				{
					var modal = top[0];
					Console.WriteLine($"  ENUMERATED: {distinct.Count} values, modal `{modal.Key}` on {100.0 * modal.Count() / values.Count:F0}% of spawns"
						+ " - the resting value, and what we should send absent a reason not to.");
				}
			}
		}

		// Flattens a protobuf message to name -> printed value, one entry per scalar leaf. Collections are
		// reported by COUNT: their contents are a separate question (equipment has its own lifted table) and a
		// per-element key would drown the per-field one this report exists to answer.
		//
		// ⚠️ PRESENCE IS A VALUE, and it has to be read through `ShouldSerializeX()` rather than off the property.
		// protobuf-net generates `get => __pbn__X.GetValueOrDefault()`, so an UNSET optional field reads back as 0
		// and is indistinguishable from one retail actually sent as 0. Both reports were scoring it that way, which
		// silently merged "the type does not carry this field" into "the type sends zero" - for
		// `npc_stats.spawn_animation` that is 904 of 1018 types folded into the modal bucket, i.e. most of what
		// looked like a 92% agreement. Presence is load-bearing everywhere else in this codebase (`Lift` copies a
		// field only when it is set, which is what keeps a rebuilt body byte-identical), so it is load-bearing here.
		private static List<(string Field, string Value)> FieldValues(object? message, string prefix = "", int depth = 0)
		{
			var values = new List<(string, string)>();
			if (message == null || depth > 3)
				return values;

			foreach (var prop in message.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
			{
				if (prop.GetIndexParameters().Length > 0)
					continue;

				object? v;
				try
				{
					v = prop.GetValue(message);
				}
				catch (System.Reflection.TargetInvocationException)
				{
					continue; // protobuf-net throws on an unset field with no default
				}

				var name = prefix + prop.Name;
				switch (v)
				{
					case System.Collections.ICollection c:
					{
						values.Add((name + " count", c.Count.ToString()));
						continue;
					}

					case null:
					{
						values.Add((name, "(absent)"));
						continue;
					}
				}

				if (message.GetType().GetMethod("ShouldSerialize" + prop.Name, Type.EmptyTypes) is { } present
					&& present.Invoke(message, null) is false)
				{
					values.Add((name, "(unset)"));
					continue;
				}

				if (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string) || prop.PropertyType.IsEnum)
					values.Add((name, Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""));
				else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(byte[]))
					values.AddRange(FieldValues(v, name + ".", depth + 1));
			}

			return values;
		}

		// --report field-predictors [--report-arg <field name>]   (default: MovementMode)
		//
		// `field-stability` says WHERE a captured NPC field lives - type, placement, or neither. This asks the question
		// that comes next, and only for the fields it condemns: a field that is rolled per spawn event belongs in no
		// column, but it still has to be SENT, so is it COMPUTED from something else the same spawn already carries?
		// (Gitea #605.) If it is, the answer is not "store it" or "send the mode" but "derive it", and no column should
		// exist at all.
		//
		// The measure is PURITY: group every sighting by the candidate predictor's value and ask, within each group,
		// what share of sightings agree on the target's value. 100% means the predictor DETERMINES the target over the
		// corpus. The baseline to beat is printed first - it is the target's own modal share, i.e. what a constant
		// scores - because a predictor that merely reaches the baseline has explained nothing.
		//
		// ⚠️ THE TRAP, and it is the whole reason cardinality is in the table: purity rises for free with the number of
		// distinct predictor values. A predictor as fine-grained as the sample itself (a handle, a position, a float
		// timer) partitions every sighting into its own group and scores 100% while explaining nothing at all. So a
		// hit is only interesting when it is BOTH pure and coarse, and the report sorts by purity but prints the
		// distinct-value count next to it and marks anything above a twentieth of the sample count as `(fine)`.
		//
		// ⚠️ Candidates are NOT only the character message's own fields. The spawn body also carries the entity's
		// MOVE TASK (SpawnPostIir.MoveTask, #477), which is the one part of a spawn that describes what the NPC is
		// DOING rather than what it is - so the move plan's mode, its path flags and its waypoint count are added as
		// synthetic predictors. For a movement/gait field those are the hypothesis worth testing first, and they are
		// invisible to a report that only walks the protobuf.
		private static void FieldPredictors(IReadOnlyList<NamedCapture> captures, string? target)
		{
			target = string.IsNullOrWhiteSpace(target) ? "MovementMode" : target.Trim();

			// One row per SIGHTING - deliberately not deduped by placement, unlike the type table in field-stability.
			// The fields this report is for are rolled per spawn EVENT, so every event is a separate observation of
			// the thing being explained; collapsing them would throw away the variation being measured.
			var rows = new List<(string Target, Dictionary<string, string> Predictors)>();
			var names = new List<(string Target, string Name)>();
			var captureOf = new List<string>();
			var players = new List<(string MovementMode, string Target)>();
			// (phase, target value) -> sightings. See the census note in the loop.
			var byPhase = new Dictionary<(string Phase, string Value), int>();
			var skipped = 0;

			foreach (var capture in captures)
			{
				var sawPoll = false;
				uint map = 0;
				foreach (var p in capture.Packets)
				{
					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
					{
						if (p.TryParse(PlayfieldFCodec.Parse, out var change))
							map = change.MapId;

						sawPoll = false;
						continue;
					}

					if (!p.IsDownload && p.WireOpcode == NpcSpawnExtractor.QuestMarkerQueryWireOpcode)
					{
						sawPoll = true;
						continue;
					}

					if (map == 0 || p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					SimpleCharFullUpdateIIR captured;
					try
					{
						captured = SimpleCharFullUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue;
					}

					// ⭐ The SPAWN PHASE census, and it runs on BOTH phases where the measurement below runs only on
					// the login burst (#607). That restriction is right for "is this field computed from something
					// else the spawn carries" - the burst is the set the importer lifts through - but it makes the
					// most obvious hypothesis about a field like `npc_stats.SpawnAnimation` UNTESTABLE by
					// construction: "play the spawn-in animation" is a property of the spawn EVENT, i.e. of whether
					// the NPC is APPEARING or was already standing there, and every row here is the second case.
					if (captured is { Handle: < EntityHandles.CharacterInstanceBase, Character.player_stats: null })
					{
						var pv = FieldValues(captured.Character).ToDictionary(v => v.Field, v => v.Value, StringComparer.Ordinal);
						var phase = sawPoll ? "mid-session" : "login burst";
						byPhase[(phase, pv.GetValueOrDefault(target, "(absent)"))] = byPhase.GetValueOrDefault((phase, pv.GetValueOrDefault(target, "(absent)"))) + 1;
					}

					if (sawPoll)
						continue;

					if (captured.Handle >= EntityHandles.CharacterInstanceBase || captured.Character?.player_stats != null)
					{
						// PLAYERS are excluded from the measurement - their scaffold is a different grammar and they
						// would swamp the NPC sample - but they are the independent SECOND family, so a candidate
						// found on NPCs is worth re-checking against them. Kept as a bare (candidate, target) tally.
						var pv = FieldValues(captured.Character).ToDictionary(v => v.Field, v => v.Value, StringComparer.Ordinal);
						if (pv.TryGetValue(target, out var pt) && pt != "(absent)")
							players.Add(($"{captured.Post.MovementMode}", pt));
						continue;
					}

					var values = FieldValues(captured.Character).ToDictionary(v => v.Field, v => v.Value, StringComparer.Ordinal);
					foreach (var (name, value) in MoveTaskPredictors(captured.Post))
						values[name] = value;

					if (!values.TryGetValue(target, out var targetValue) || targetValue == "(absent)")
					{
						skipped++;
						continue;
					}

					values.Remove(target);
					rows.Add((targetValue, values));
					names.Add((targetValue, captured.Character?.Name ?? ""));
					captureOf.Add(capture.Name);
				}
			}

			// Printed FIRST, because when it separates it answers the question the purity table cannot even ask.
			if (byPhase.Count > 0)
			{
				Console.WriteLine($"`{target}` by SPAWN PHASE - is it a property of the spawn EVENT rather than of the entity? (#607)");
				foreach (var phase in new[] { "login burst", "mid-session" })
				{
					var rowsIn = byPhase.Where(kv => kv.Key.Phase == phase).OrderByDescending(kv => kv.Value).ToList();
					var n = rowsIn.Sum(kv => kv.Value);
					Console.WriteLine($"  {phase,-12} {n,6} spawn(s): {(n == 0 ? "-" : string.Join(", ", rowsIn.Select(kv => $"{kv.Key.Value} x{kv.Value} ({100.0 * kv.Value / n:F1}%)")))}");
				}

				Console.WriteLine();
			}

			Console.WriteLine($"field-predictors: is `{target}` COMPUTED from something else the same spawn carries?");
			Console.WriteLine("⚠️ The purity table below is LOGIN-BURST ONLY, which is the set the importer lifts through.");
			Console.WriteLine("Purity = group the sightings by a candidate's value; within each group, the share that agree on");
			Console.WriteLine("the target. 100% = the candidate determines the target. Beat the baseline or nothing was learnt.");
			Console.WriteLine();

			if (rows.Count == 0)
			{
				Console.WriteLine($"No captured NPC spawn carries `{target}` ({skipped} spawns had it absent). "
					+ "Names come from the flattened CoreCharacterIIR - run --report field-stability for the list.");
				return;
			}

			var targetCounts = rows.GroupBy(r => r.Target, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToList();
			var baseline = 100.0 * targetCounts[0].Count() / rows.Count;
			Console.WriteLine($"{rows.Count} sightings ({skipped} skipped: the field was absent), target values: "
				+ string.Join(", ", targetCounts.Select(g => $"{g.Key} (x{g.Count()})")));
			Console.WriteLine($"BASELINE {baseline:F1}% - what a constant `{targetCounts[0].Key}` already scores.");
			Console.WriteLine();
			Console.WriteLine($"{"candidate",-44} {"purity",7} {"values",7}  mapping (pure groups only)");

			// A candidate is only comparable if it is present on every row: a predictor observed on a subset is
			// scored against a different denominator, and would sort above one that covers the whole corpus.
			var candidates = rows[0].Predictors.Keys.Where(k => rows.All(r => r.Predictors.ContainsKey(k))).ToList();
			var scored = new List<(string Name, double Purity, int Cardinality, string Mapping)>();
			foreach (var candidate in candidates)
			{
				var groups = rows.GroupBy(r => r.Predictors[candidate], StringComparer.Ordinal).ToList();
				var agreeing = groups.Sum(g => g.GroupBy(r => r.Target, StringComparer.Ordinal).Max(t => t.Count()));
				var purity = 100.0 * agreeing / rows.Count;

				// Print the mapping only when it is small enough to READ - a 500-row map is noise, and a candidate
				// that needs one is failing the coarseness test anyway.
				var mapping = groups.Count <= 6
					? string.Join(" ", groups.OrderByDescending(g => g.Count()).Select(g =>
					{
						var t = g.GroupBy(r => r.Target, StringComparer.Ordinal).OrderByDescending(x => x.Count()).ToList();
						return $"{g.Key}=>{(t.Count == 1 ? t[0].Key : string.Join("/", t.Select(x => $"{x.Key}x{x.Count()}")))}";
					}))
					: "";
				scored.Add((candidate, purity, groups.Count, mapping));
			}

			var fineThreshold = rows.Count / 20;
			foreach (var s in scored.OrderByDescending(s => s.Purity).ThenBy(s => s.Cardinality).Take(30))
			{
				Console.WriteLine($"{(s.Name.Length > 43 ? s.Name[..43] : s.Name),-44} {s.Purity,6:F1}% {s.Cardinality,7}"
					+ $"{(s.Cardinality > fineThreshold ? " (fine)" : "       ")} {s.Mapping}");
			}

			Console.WriteLine();
			Console.WriteLine($"`(fine)` = more than {fineThreshold} distinct values over {rows.Count} sightings; its purity is an artefact of");
			Console.WriteLine("granularity, not an explanation. A real answer is a COARSE candidate at ~100%.");

			// The EXCEPTIONS to the best coarse candidate, in full. A candidate at 99.98% is either a rule with a
			// mechanism behind its exception or a coincidence, and the count alone cannot tell them apart - so print
			// every disagreeing sighting rather than its tally (the #549 lesson: a report has to let you re-cut an
			// anomaly by a field you did not think to print).
			var best = scored.Where(s => s.Cardinality <= fineThreshold).OrderByDescending(s => s.Purity).FirstOrDefault();
			if (best is { Name: not null, Purity: > 90.0 and < 100.0 })
			{
				Console.WriteLine();
				Console.WriteLine($"--- every sighting that disagrees with `{best.Name}` ({best.Purity:F2}%) ---");
				var majority = rows
					.GroupBy(r => r.Predictors[best.Name], StringComparer.Ordinal)
					.ToDictionary(g => g.Key, g => g.GroupBy(r => r.Target, StringComparer.Ordinal).OrderByDescending(t => t.Count()).First().Key, StringComparer.Ordinal);

				for (var i = 0; i < rows.Count; i++)
				{
					var predicted = majority[rows[i].Predictors[best.Name]];
					if (predicted == rows[i].Target)
						continue;

					Console.WriteLine($"  {names[i].Name} in {captureOf[i]}: {best.Name}={rows[i].Predictors[best.Name]} predicts {target}={predicted}, sent {rows[i].Target}");
					Console.WriteLine($"    movetask={rows[i].Predictors.GetValueOrDefault("[movetask].Mode")}"
						+ $" leaf={rows[i].Predictors.GetValueOrDefault("[movetask].LeafMode")}"
						+ $" buffs={rows[i].Predictors.GetValueOrDefault("[post].BuffRecords count")}"
						+ $" template={rows[i].Predictors.GetValueOrDefault("npc_stats.TemplateId")}"
						+ $" bccState={rows[i].Predictors.GetValueOrDefault("Stats.BccState")}"
						+ $" animIdle={rows[i].Predictors.GetValueOrDefault("Bcc.AnimIdleId")}"
						+ $" animAction={rows[i].Predictors.GetValueOrDefault("Bcc.AnimActionId")}");
				}
			}

			// The PLAYER family, as a cross-check on the post-IIR header word. A candidate measured on one family and
			// confirmed on the other is a protocol fact; one measured on NPCs alone could be an NPC convention.
			if (players.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"--- cross-check: PLAYER spawns, [post].MovementMode vs `{target}` ---");
				foreach (var g in players.GroupBy(p => (p.MovementMode, p.Target)).OrderByDescending(g => g.Count()))
					Console.WriteLine($"  MovementMode={g.Key.MovementMode}, {target}={g.Key.Target}: x{g.Count()}");
			}

			// Is the instability RUNTIME? `field-stability` scores a field over every sighting, so an entity caught
			// mid-action scores as "the type has no value for this". That conflates two different verdicts: a field
			// with no type-level value at all, and one whose type-level value is its RESTING value, temporarily
			// displaced by whatever the entity is doing. The two are told apart by re-running the type grouping over
			// FRESH sightings only - an empty move-task slot and no active buffs, i.e. an entity that has just been
			// spawned and is doing nothing. If the field is type-stable there, a resting value exists and the type row
			// may hold it; if it is not, no column can (Gitea #605).
			var fresh = rows
				.Select((r, i) => (Row: r, Template: r.Predictors.GetValueOrDefault("npc_stats.TemplateId", "")))
				.Where(x => x.Row.Predictors.GetValueOrDefault("[movetask].Mode") == "(empty slot)"
					&& x.Row.Predictors.GetValueOrDefault("[post].BuffRecords count") == "0"
					&& x.Template.Length > 0)
				.GroupBy(x => x.Template, StringComparer.Ordinal)
				.Where(g => g.Count() > 1)
				.ToList();

			if (fresh.Count > 0)
			{
				var stable = fresh.Count(g => g.Select(x => x.Row.Target).Distinct(StringComparer.Ordinal).Count() == 1);
				Console.WriteLine();
				Console.WriteLine($"--- `{target}` over FRESH sightings only (empty move-task slot, no buffs) ---");
				Console.WriteLine($"{stable} of {fresh.Count} templates sighted more than once fresh hold ONE value: {100.0 * stable / fresh.Count:F1}% type-stable.");
				Console.WriteLine("Compare this with the same field's figure in --report field-stability, which is over ALL sightings.");
				foreach (var g in fresh.Where(g => g.Select(x => x.Row.Target).Distinct(StringComparer.Ordinal).Count() > 1).Take(10))
					Console.WriteLine($"  UNSTABLE FRESH: template {g.Key} -> {string.Join("/", g.Select(x => x.Row.Target).Distinct(StringComparer.Ordinal))}");
			}

			// WHO sends each value. A purity table says a candidate explains the field; it cannot say what the field
			// MEANS. The NPCs behind each value can - "every sighting of value 3 is a mounted rider" is the kind of
			// statement no cross-tab produces, and it is what turns a correlation into a name.
			Console.WriteLine();
			Console.WriteLine($"--- who sends each `{target}` value ---");
			foreach (var group in names.GroupBy(n => n.Target, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
			{
				var byName = group.GroupBy(n => n.Name, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToList();
				Console.WriteLine();
				Console.WriteLine($"{target} = {group.Key}: {group.Count()} sightings over {byName.Count} distinct NPC names");
				Console.WriteLine($"  {string.Join(", ", byName.Take(15).Select(g => $"{g.Key} (x{g.Count()})"))}");
			}
		}

		// The synthetic predictors that come from the spawn's MOVE TASK rather than from its character message. Every
		// one of these is a fact about what the entity is DOING at the instant of the spawn - which is the only kind of
		// candidate that can explain a per-spawn-event field - and the move plan is the sole such fact the body holds.
		//
		// Buckets, not raw values, on everything continuous: a duration in milliseconds partitions the corpus into one
		// group per sighting and would score a meaningless 100% (see the coarseness warning on FieldPredictors).
		private static IEnumerable<(string Name, string Value)> MoveTaskPredictors(SpawnPostIir post)
		{
			yield return ("[post].HasMoveTaskSlot", post.HasMoveTaskSlot.ToString());
			yield return ("[post].MovementMode", post.MovementMode.ToString());
			yield return ("[post].BuffRecords count", post.BuffRecords.Count.ToString());

			var task = post.MoveTask;
			yield return ("[movetask].Mode", task == null ? post.HasMoveTaskSlot ? "(empty slot)" : "(no slot)" : task.Mode.ToString());
			if (task == null)
			{
				yield return ("[movetask].PathFlags", "(none)");
				yield return ("[movetask].ByReference", "(none)");
				yield return ("[movetask].Waypoints", "(none)");
				yield return ("[movetask].HeaderFlags", "(none)");
				yield break;
			}

			// A mode-4 ListMoveTask wraps the real plan, so read the leaf: the wrapper's own mode says nothing about
			// what the entity is doing, and every list in the corpus is a plan plus its modifiers.
			var leaf = task;
			while (leaf is { Mode: 4, Children.Count: > 0 })
				leaf = leaf.Children[0];

			yield return ("[movetask].LeafMode", leaf.Mode.ToString());
			yield return ("[movetask].PathFlags", leaf.Mode == 1 ? leaf.PathFlags.ToString() : "(not a path)");
			yield return ("[movetask].ByReference", leaf.Mode == 1 ? ((leaf.PathFlags & 1) != 0).ToString() : "(not a path)");
			yield return ("[movetask].HeaderFlags", leaf.HeaderFlags.ToString());
			yield return ("[movetask].Waypoints", leaf.Mode != 1
				? "(not a path)"
				: (leaf.PathFlags & 1) != 0
					? "(by reference)"
					: (leaf.Waypoints.Count / 3) switch { 0 => "0", 1 => "1", <= 4 => "2-4", _ => "5+" });
		}

		private static List<string> FieldDiff(object? theirs, object? ours, string prefix = "", int depth = 0)
		{
			var diffs = new List<string>();
			if (depth > 3 || (theirs == null && ours == null))
				return diffs;

			if (theirs == null || ours == null)
			{
				diffs.Add($"{prefix.TrimEnd('.')} {(theirs == null ? "absent" : "present")} vs {(ours == null ? "absent" : "present")}");
				return diffs;
			}

			foreach (var prop in theirs.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
			{
				if (prop.GetIndexParameters().Length > 0)
					continue;

				object? a, b;
				try
				{
					a = prop.GetValue(theirs);
					b = prop.GetValue(ours);
				}
				catch (System.Reflection.TargetInvocationException)
				{
					continue; // protobuf-net throws on an unset field with no default; nothing to compare
				}

				var name = prefix + prop.Name;
				if (a is System.Collections.ICollection ca && b is System.Collections.ICollection cb)
				{
					if (ca.Count != cb.Count)
						diffs.Add($"{name} count {ca.Count} vs {cb.Count}");

					continue;
				}

				if (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string) || prop.PropertyType.IsEnum)
				{
					if (!Equals(a, b))
						diffs.Add($"{name} {a} vs {b}");

					continue;
				}

				if (prop.PropertyType.IsClass && prop.PropertyType != typeof(byte[]))
					diffs.AddRange(FieldDiff(a, b, name + ".", depth + 1));
			}

			return diffs;
		}

		// One NPC name's spawn-diff outcome. A class rather than a tuple because the counters are bumped one at a
		// time down a long loop, and a record struct there reads as five re-assignments of the whole verdict.
		private sealed class SpawnVerdict
		{
			public int Same;
			public int Differ;
			public int NoRow;
			public int Failed;
			public string? Note;
		}

		private static void NpcGenericStats(IReadOnlyList<NamedCapture> captures)
		{
			var lengths = new Dictionary<int, int>();
			var samples = new List<string>();
			int spawns = 0, withStats = 0;

			foreach (var c in captures)
			foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
			{
				var body = e.Data is { Length: >= 12 }
					? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
					: null;
				if (body == null || !SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					continue;

				spawns++;
				var stats = read.Message.GenericStats;
				if (stats is not { Length: > 0 })
					continue;

				withStats++;
				lengths[stats.Length] = lengths.GetValueOrDefault(stats.Length) + 1;
				if (samples.Count < 8)
					samples.Add($"{e.Name} (level {read.Message.Stats?.Level}): [{string.Join(", ", stats.Take(16))}]");
			}

			Console.WriteLine($"npc-generic-stats: {spawns} NPC spawns read, {withStats} carry generic_stats");
			foreach (var (length, n) in lengths.OrderByDescending(k => k.Value))
				Console.WriteLine($"  length {length}: {n}");

			foreach (var sample in samples)
				Console.WriteLine($"  {sample}");
		}

		private static void Equip(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
				{
					var body = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
						: null;
					if (body == null || !SpawnBodyReader.TryReadCharacterMessage(body, out var read))
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
			using var rdb = OpenRdb(rdbDir);
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
			foreach (var e in rdb.EntriesOfType(1030002).Where(rdb.CanRead))
			{
				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // the container is not an IOz1/zlib page
				}

				if (body.Length == 0)
					continue; // a CDN page the mirror could not serve (RdbReader returns empty rather than throwing)

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
			using var rdb = OpenRdb(rdbDir);

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

		// --report vitals-shape [--report-arg <capture-name-substring>]
		//
		// WHICH stat ids retail puts in a 0x96B8DC59 StatUpdateIIR, grouped by the RECORD SET the block carries and
		// by whose handle it is on (#586). We send all six vitals ids - HpCur/HpMax/StamCur/StamMax/ManaCur/ManaMax -
		// for every entity; the claim this measures is that retail sends only the three CURRENTS on a non-player
		// handle, and six only on the player's own.
		//
		// The player's handle is not labelled on the wire, so it is DERIVED rather than assumed: it is the handle the
		// capture's FullPlayerCharacter snapshot (0x737A6DF9) is about, which is the one entity a session has exactly
		// one of. A capture with no snapshot is reported separately rather than guessed at.
		private static void VitalsShape(IReadOnlyList<NamedCapture> captures, string? arg)
		{
			var byShape = new Dictionary<(bool Player, string Ids), int>();
			var examples = new Dictionary<(bool Player, string Ids), string>();
			var noSnapshot = 0;

			foreach (var c in captures)
			{
				if (!string.IsNullOrWhiteSpace(arg) && !c.Name.Contains(arg, StringComparison.OrdinalIgnoreCase))
					continue;

				// The session's own character handle, from its FullPlayerCharacter snapshot.
				uint? playerHandle = null;
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: FullPlayerCharacterCodec.Opcode } || p.Record.Length < 12)
						continue;

					// [type][entityClass][handle] - the same prologue StatUpdateIIR carries.
					playerHandle = p.Record.ReadU32Be(8);
					break;
				}

				if (playerHandle == null)
				{
					noSnapshot++;
					continue;
				}

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: StatUpdateCodec.Opcode })
						continue;

					StatUpdateIIR update;
					try
					{
						update = StatUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue; // a body the codec cannot account for is not evidence about the id SET
					}

					var ids = string.Join(",", update.Stats.Records.Select(e => $"0x{(uint)e.StatId:X4}").OrderBy(x => x));
					var key = (update.EntityHandle == playerHandle, ids);
					byShape[key] = byShape.GetValueOrDefault(key) + 1;
					if (!examples.ContainsKey(key))
						examples[key] = c.Name;
				}
			}

			Console.WriteLine($"StatUpdateIIR (0x{StatUpdateCodec.Opcode:X8}) record sets, by whose handle they are on");
			if (noSnapshot > 0)
				Console.WriteLine($"  ({noSnapshot} capture(s) skipped - no FullPlayerCharacter snapshot, so no player handle to compare against)");
			Console.WriteLine();

			foreach (var player in new[] { true, false })
			{
				Console.WriteLine(player ? "--- the session's OWN character" : "--- every OTHER handle (NPCs, other players)");
				var rows = byShape.Where(kv => kv.Key.Player == player).OrderByDescending(kv => kv.Value).ToList();
				if (rows.Count == 0)
				{
					Console.WriteLine("    (none)");
					Console.WriteLine();
					continue;
				}

				foreach (var (key, n) in rows)
					Console.WriteLine($"    {n,7}  [{key.Ids}]   e.g. {examples[key][..Math.Min(46, examples[key].Length)]}");

				Console.WriteLine();
			}
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
					if (!p.IsIirCarrier || p.SubOpcode != PlayfieldFCodec.Opcode || p.Bytes.Length < 16)
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
					ops[(p.IsDownload, p.WireOpcode)] = ops.GetValueOrDefault((p.IsDownload, p.WireOpcode)) + 1;
					subs[(p.IsDownload, p.WireOpcode, p.SubOpcode)] = subs.GetValueOrDefault((p.IsDownload, p.WireOpcode, p.SubOpcode)) + 1;
					opCols.Add((p.WireOpcode, p.IsDownload));
					subCols.Add((p.WireOpcode, p.SubOpcode, p.IsDownload));
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
					var k = (p.IsDownload, p.WireOpcode, p.SubOpcode);
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
			_db ??= CliConfig.ResolveDatabase(_options.Configuration);
			return DatabaseExtensions.CreateContext(_db.Value.Provider, _db.Value.Connection);
		}

		// RDB -> 3D-model asset graph (Gitea #275, Phase 0). With no --report-arg: a corpus summary (how many
		// MESHINDEXFILE manifests resolve to geometry, how their textures are split local vs CDN). With --report-arg
		// <meshIndexId>: print that model's graph (RenderMesh id, materials, texture refs) and, if --out <dir> is given,
		// extract its FCTX textures to DDS (+ --dump-mesh to also write the raw 1010001 geometry blob for the Phase 1 spike).
		private static void RdbAssets(string rdbDir, string? arg, string? outDir, bool dumpMesh)
		{
			using var rdb = OpenRdb(rdbDir);

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
				if (e.Size == 0 || !rdb.CanRead(e))
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
		// The RDB is OPTIONAL for a report that only enriches its output with it (scaffold-heap decrypts the LDB
		// strings it finds when it can, and names them when it cannot). RdbDir() throws, which is right for a report
		// that cannot run without it and wrong for one that degrades.
		private static string? RdbDirOrNull()
		{
			return _options.Configuration[Keys.ContentRdbDir];
		}

		// Every report opens the RDB THROUGH the CDN mirror. A reader built without a proxy base silently degrades to
		// the LOCAL records only, and for several types that is a minority of the game: 577 of the 817 NPC spawn
		// layouts (1010506), 580 of 653 in-game map tiles (1010013) and 276 of 520 tilemaps (1070003) are CDN-only.
		// Honours `--rdb-proxy`, which the reports used to ignore in favour of the hardcoded default.
		private static RdbReader OpenRdb(string? rdbDir = null)
		{
			return new RdbReader(rdbDir ?? RdbDir(), _options.RdbProxy ?? _options.Configuration.CdnPublicAddress);
		}

		private static string RdbDir()
		{
			return _options.Configuration[Keys.ContentRdbDir] ??
				throw new InvalidOperationException(
					"The client's RDB folder is not configured - set Content:RdbDir in faolan.json, the Content__RdbDir environment variable, or pass --Content:RdbDir <dir>.");
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

			using var rdb = OpenRdb(rdbDir);
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
		// The feat/ability catalog's RDB type.
		private const uint FeatTemplateType = 1000020;

		// Harvests the feat EFFECT element's kind -> width table empirically (#251), and CHECKS it against the width
		// the kind's key list predicts (#591).
		//
		// The element opens with a KIND tag (0xCF..) and its width VARIES with that kind, which is what defeats a
		// fixed-stride walk and left 3.1% of effect rows on stats the client cannot name. The width is now KNOWN
		// rather than fitted - an element is `8 + len(keyList)` words and the key lists come from the client
		// (GameActionTypes) - so what this measures is a CHECK on that, over shipped bytes.
		//
		// ⚠️ What it measures is an upper BOUND, not the width: in a record whose effect list holds exactly one
		// element, `recordLength - elementStart` also swallows anything the record carries AFTER the element list.
		// That is why the surplus dump matters - where the two disagree it has so far been trailing record content
		// (a list frame, a mesh name), not element bytes.
		// --report rdb-field-coverage [--report-arg <rdbType>]
		//
		// Every property-table field a record type carries, how many records carry it, and whether the importer
		// READS it. Written after #590, where a shipped field (the spell line, 128) turned out to be sitting in plain
		// sight in a table we already parsed - the question "what else is in here that we walk straight past?"
		// deserves a measurement rather than a guess.
		//
		// Defaults to the spell type (1040005); pass `<type>` for another, or `<type>:<field>[,<field>...]` to DRILL
		// into fields - each one's value distribution and named sample records, which is what turns "unread" into
		// "worth reading". Several fields drill in ONE pass over the type; a pass costs a full decompress of every
		// record (21,502 for spells), so drilling them one at a time is the same measurement for N times the wait.
		//
		// Append `x<field>` to CROSS-TABULATE the drilled fields against another one - `1040005:348x847` prints, for
		// each value of 847, the distribution of 348 among the records carrying both. That is the shape that answers
		// "does this field VARY WITH that one", which a pair of independent histograms cannot.
		//
		// ⚠️ It reads the ASCENDING property grammar. That is the established one for SPELL records; for 1000020 the
		// plaintext half is feats and the run is ascending there too, but an ITEM template's table strictly DESCENDS
		// (RdbItemImporter.TryReadPropertyField) and is not covered by this - so a 1000020 run measures the feats.
		//
		// With `--out <dir>` a drill also writes `rdb-<type>-field-<n>.tsv` - EVERY carrying record's id, name and
		// value, not the 14 samples. A histogram says what values exist; deciding whether an unread field is the
		// second source for a column we already fill needs the per-record join, and the 14-sample cap cannot do it
		// (#633).
		private static void RdbFieldCoverage(string rdbDir, string? arg, string? outDir)
		{
			var spec = (arg ?? "").Split(':', 2, StringSplitOptions.TrimEntries);
			var type = spec.Length > 0 && uint.TryParse(spec[0], out var t) ? t : SpellTemplateType;
			var drillSpec = spec.Length == 2 ? spec[1] : "";
			var againstSplit = drillSpec.Split('x', 2, StringSplitOptions.TrimEntries);
			var against = againstSplit.Length == 2 && uint.TryParse(againstSplit[1], out var a) ? a : 0u;
			var drills = againstSplit[0]
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(s => uint.TryParse(s, out var d) ? d : 0u)
				.Where(d => d != 0)
				.ToArray();
			// What each importer actually consumes today, so the report can say READ vs unread rather than just
			// enumerating. Keep in step with the importers; a field that moves here without moving there is exactly
			// the drift this report exists to surface.
			// ⚠️ Keep this in step with RdbSpellImporter's `*Field` constants - it is a hand-maintained list, and it
			// had DRIFTED: it claimed 79 (icon), 129, 321 and 551 were read when nothing reads any of them, which
			// inflated "read by the importer" from 13 to 17 and hid four gaps behind a green-looking column (#633).
			// The names on 129 and 551 were wrong too - the client calls them SpellCriticalDamage and StackingOrder.
			// It drifts the OTHER way too: 447 was imported by #349 and never added here, so the report was calling a
			// read field unread.
			var read = type == SpellTemplateType
				? new Dictionary<uint, string>
				{
					[8] = "duration", [123] = "rank", [125] = "level", [128] = "spell line (#590)",
					[132] = "mana cost, mode from the flags word (#629/#630)", [186] = "mount creature (#627)",
					[287] = "range", [294] = "cast time", [447] = "channeling interval (#349)",
					[347] = "splash radius (#626)", [348] = "splash damage (#626)", [349] = "splash max targets",
					[499] = "mount quality (#627)", [847] = "scaling attribute (#592)",
					[129] = "spell critical damage (#633)", [147] = "spell critical chance (#633)"
				}
				: new Dictionary<uint, string> { [74] = "value", [104] = "bag", [499] = "quality", [972] = "primary attribute", [1331] = "resource bag" };

			using var rdb = OpenRdb(rdbDir);
			var records = 0;
			var counts = new Dictionary<uint, int>();
			var ranges = new Dictionary<uint, (uint Min, uint Max)>();
			// drilled[field][value] = how many records carry that value; crossed[field][(against, value)] likewise but
			// split by the -x- field's value on the SAME record.
			var drilled = drills.ToDictionary(f => f, _ => new Dictionary<uint, int>());
			var crossed = drills.ToDictionary(f => f, _ => new Dictionary<(uint Against, uint Value), int>());
			var samples = drills.ToDictionary(f => f, _ => new List<string>());
			// --out: every carrying record, not just the samples (id, name, value).
			var rows = outDir != null ? drills.ToDictionary(f => f, _ => new List<(uint Id, string Name, uint Value)>()) : null;

			foreach (var e in rdb.EntriesOfType(type))
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
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != type)
					continue;

				records++;
				// The whole record's fields first: a cross-tab needs the -x- field's value on this record, and the
				// property run is ascending, so a field drilled BEFORE the one it is crossed against is not yet known
				// while walking. A repeated field is last-wins, matching the importers.
				var fields = new Dictionary<uint, uint>();
				foreach (var (field, value) in RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12)))
				{
					counts[field] = counts.GetValueOrDefault(field) + 1;
					var (min, max) = ranges.TryGetValue(field, out var r) ? r : (uint.MaxValue, 0u);
					ranges[field] = (Math.Min(min, value), Math.Max(max, value));
					fields[field] = value;
				}

				foreach (var drill in drills)
				{
					if (!fields.TryGetValue(drill, out var value))
						continue;

					drilled[drill][value] = drilled[drill].GetValueOrDefault(value) + 1;
					if (against != 0 && fields.TryGetValue(against, out var againstValue))
					{
						var key = (againstValue, value);
						crossed[drill][key] = crossed[drill].GetValueOrDefault(key) + 1;
					}

					if (samples[drill].Count < 14 || rows != null)
					{
						var (name, _) = RdbFeatImporter.ReadNameAndDescription(body);
						rows?[drill].Add((e.Id, name, value));
						if (samples[drill].Count < 14)
						{
							var cross = against != 0 && fields.TryGetValue(against, out var av) ? $"   [{against}={av}]" : "";
							samples[drill].Add($"{e.Id} {name} = {value}{cross}");
						}
					}
				}
			}

			if (drills.Length > 0)
			{
				foreach (var drill in drills)
				{
					Console.WriteLine($"RDB type {type} field {drill}: {drilled[drill].Values.Sum()} records carry it, {drilled[drill].Count} distinct values");
					Console.WriteLine();
					Console.WriteLine("value distribution (top 20):");
					foreach (var (value, n) in drilled[drill].OrderByDescending(p => p.Value).Take(20))
						Console.WriteLine($"  {value,12} x{n}");

					if (against != 0)
					{
						Console.WriteLine();
						Console.WriteLine($"crossed with field {against} (top 10 values of {drill} per {against}):");
						foreach (var group in crossed[drill].GroupBy(p => p.Key.Against).OrderBy(g => g.Key))
						{
							var total = group.Sum(p => p.Value);
							Console.WriteLine($"  {against}={group.Key}  ({total} records, {group.Count()} distinct {drill})");
							foreach (var (key, n) in group.OrderByDescending(p => p.Value).Take(10))
								Console.WriteLine($"      {key.Value,12} x{n}");
						}
					}

					Console.WriteLine();
					Console.WriteLine("samples:");
					foreach (var line in samples[drill])
						Console.WriteLine($"  {line}");

					if (rows != null)
					{
						Directory.CreateDirectory(outDir!);
						var path = Path.Combine(outDir!, $"rdb-{type}-field-{drill}.tsv");
						File.WriteAllLines(path, rows[drill]
							.OrderBy(r => r.Id)
							.Select(r => $"{r.Id}\t{r.Name}\t{r.Value}")
							.Prepend("id\tname\tvalue"));
						Console.WriteLine($"  -> {path} ({rows[drill].Count} records)");
					}

					Console.WriteLine();
				}

				return;
			}

			Console.WriteLine($"RDB type {type}: {records} records, {counts.Count} distinct property fields");
			Console.WriteLine($"  read by the importer: {counts.Keys.Count(read.ContainsKey)}   unread: {counts.Keys.Count(f => !read.ContainsKey(f))}");
            Console.WriteLine();
			Console.WriteLine($"{"field",8} {"records",8} {"min",12} {"max",12}   what");
			foreach (var (field, n) in counts.OrderByDescending(p => p.Value))
			{
				var (min, max) = ranges[field];
				var what = read.TryGetValue(field, out var name) ? $"READ - {name}" : "";
				Console.WriteLine($"{field,8} {n,8} {min,12} {max,12}   {what}");
			}
		}

		// --report spell-line-ids [--report-arg <lineId>]
		//
		// The SPELL-LINE id every spell template carries in property-table field 128 (#590). It is what a feat's
		// modifier record references: `Decomposition` points at 41000 and `Flesh to Worms` carries 41000, so a feat's
		// "+35% on Flesh to Worms" resolves through this and nothing else.
		//
		// The id is `ConanClass x 1000 + lineIndex` - Necromancer (41) owns 41000 Flesh to Worms, 41001 Pestilential
		// Blast, 41005 Ice Strike - which is why the same line id is shared by every RANK of a spell, and why a feat
		// can name a spell without naming a rank.
		//
		// --report spell-mana-mode [--report-arg <threshold>]   (default threshold 100)
		//
		// #630: the client reads spell field 132 (the mana cost) in TWO modes - a percentage of the caster's mana
		// pool, or an absolute cost in wire hundredths - and picks between them with bit 2 of a flag word on the
		// loaded spell object (`*(uint*)(spell + 0xC8) >> 2 & 1`, FUN_008d6f70). What FILLS that word is unknown, so
		// `CombatResolver.ResolveSpellManaCost` picks the mode by MAGNITUDE instead: <= 100 is a percent.
		//
		// This asks the catalog whether any OTHER field determines the same split. For every field F the record
		// carries, it buckets the records by F's value and measures how PURE each bucket is with respect to the
		// percent/flat class. A field that IS the flag (or that carries it) partitions perfectly: every one of its
		// values sees only percents or only flats. Ranked by purity, so a partial signal is visible too.
		//
		// ⚠️ The class comes from the heuristic being tested, so this cannot CONFIRM the heuristic - only find a field
		// that agrees with it. A perfect separator would be strong evidence; the absence of one says the flag is not
		// in the ascending property run at all, which is itself worth knowing (it would point at the record's fixed
		// head, or at data outside the record).
		private static void SpellManaMode(string rdbDir, string? arg)
		{
			var threshold = uint.TryParse(arg, out var t) && t > 0 ? t : 100u;
			using var rdb = OpenRdb(rdbDir);

			// perClass[field][value] = (percents, flats) among the records carrying that field at that value.
			var perField = new Dictionary<uint, Dictionary<uint, (int Percent, int Flat)>>();
			var headWords = new Dictionary<int, Dictionary<uint, (int Percent, int Flat)>>();
			var percents = 0;
			var flats = 0;

			foreach (var e in rdb.EntriesOfType(SpellTemplateType))
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
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				var fields = RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12));
				if (!fields.TryGetValue(SpellManaCostField, out var cost) || cost == 0)
					continue; // a free spell states no mode

				var isPercent = cost <= threshold;
				if (isPercent)
					percents++;
				else
					flats++;

				// The ascending run is only ~16 fields wide and starts around word 15 of the body, so the great
				// majority of a 964-byte record is outside it. Sweep the FIXED head at word granularity too - a flag
				// word that is part of the record's struct rather than its property table would live there.
				for (var w = 0; w < HeadWords && (w + 1) * 4 <= body.Length; w++)
				{
					var word = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(w * 4, 4));
					var byValue = headWords.TryGetValue(w, out var hv) ? hv : headWords[w] = new Dictionary<uint, (int, int)>();
					var (hp, hf) = byValue.GetValueOrDefault(word);
					byValue[word] = isPercent ? (hp + 1, hf) : (hp, hf + 1);
				}

				foreach (var (field, value) in fields)
				{
					if (field == SpellManaCostField)
						continue;

					var byValue = perField.TryGetValue(field, out var bv) ? bv : perField[field] = new Dictionary<uint, (int, int)>();
					var (p, f) = byValue.GetValueOrDefault(value);
					byValue[value] = isPercent ? (p + 1, f) : (p, f + 1);
				}
			}

			Console.WriteLine($"Spell mana-cost mode split at field {SpellManaCostField} <= {threshold}: {percents} percent, {flats} flat "
			                  + $"({percents + flats} records state a cost).");
			Console.WriteLine("A field that determines the mode separates them perfectly - every value of it seeing only one class.\n");
			Console.WriteLine($"{"field",6} {"records",8} {"values",7} {"purity",8}  {"mixed values",12}");

			var ranked = perField
				.Select(kv =>
				{
					var covered = kv.Value.Sum(v => v.Value.Percent + v.Value.Flat);
					var majority = kv.Value.Sum(v => Math.Max(v.Value.Percent, v.Value.Flat));
					var mixed = kv.Value.Count(v => v.Value is { Percent: > 0, Flat: > 0 });
					return (Field: kv.Key, Covered: covered, Values: kv.Value.Count, Purity: (double)majority / Math.Max(covered, 1), Mixed: mixed);
				})
				// A field on a handful of records separates them trivially and says nothing, so rank by purity but
				// require it to cover a real share of the catalog before it counts as an answer.
				.OrderByDescending(x => x.Purity)
				.ThenByDescending(x => x.Covered)
				.ToList();

			foreach (var x in ranked.Take(25))
				Console.WriteLine($"{x.Field,6} {x.Covered,8} {x.Values,7} {x.Purity,8:P2}  {x.Mixed,12}");

			var separators = ranked.Where(x => x.Mixed == 0 && x.Covered >= (percents + flats) / 2).ToList();
			Console.WriteLine(separators.Count == 0
				? "\nNo field covering at least half the catalog separates the two modes cleanly."
				: $"\nPERFECT SEPARATORS covering >= half the catalog: {string.Join(", ", separators.Select(s => s.Field))}");

			// The client does not read a MODE field - it reads one BIT of a flag word (`spell + 0xC8 >> 2 & 1`). So
			// the value-bucket sweep above is the wrong shape if the record stores that word packed: a bitmask field
			// takes hundreds of values, each seen a handful of times, and looks like noise. Test the bits directly.
			//
			// A field ABSENT from a record is not a zero bit - it is no statement at all - so the absent records are
			// counted separately rather than folded into the 0 side, where they would manufacture agreement.
			Console.WriteLine("\nBit sweep - is some single BIT of some field the selector?\n");
			Console.WriteLine($"{"field",6} {"bit",4} {"records",8} {"agree",8}  set:pct/flat  clear:pct/flat");

			var bits = new List<(uint Field, int Bit, int Covered, double Agreement, int SetP, int SetF, int ClearP, int ClearF)>();
			foreach (var (field, byValue) in perField)
			{
				for (var bit = 0; bit < 32; bit++)
				{
					int setP = 0, setF = 0, clearP = 0, clearF = 0;
					foreach (var (value, (p, f)) in byValue)
					{
						if ((value >> bit & 1) == 1)
						{
							setP += p;
							setF += f;
						}
						else
						{
							clearP += p;
							clearF += f;
						}
					}

					var covered = setP + setF + clearP + clearF;
					if (setP + setF == 0 || clearP + clearF == 0)
						continue; // the bit never varies over the records carrying this field - it states nothing

					// Either polarity is a candidate: the bit may mean percent or mean flat.
					var agreement = Math.Max(setP + clearF, setF + clearP) / (double)covered;
					bits.Add((field, bit, covered, agreement, setP, setF, clearP, clearF));
				}
			}

			foreach (var b in bits.OrderByDescending(x => x.Agreement).ThenByDescending(x => x.Covered).Take(15))
				Console.WriteLine($"{b.Field,6} {b.Bit,4} {b.Covered,8} {b.Agreement,8:P2}  {b.SetP,5}/{b.SetF,-5} {b.ClearP,7}/{b.ClearF,-5}");

			var perfectBits = bits.Where(b => b.Agreement >= 1.0 && b.Covered >= (percents + flats) / 2).ToList();
			Console.WriteLine(perfectBits.Count == 0
				? "\nNo single bit of any field covering at least half the catalog selects the mode."
				: $"\nSELECTOR BITS: {string.Join(", ", perfectBits.Select(b => $"field {b.Field} bit {b.Bit}"))}");

			// Third shape: the flag may not be a VALUE at all. A field that is written only when the spell is in one
			// mode makes its own PRESENCE the statement, and both sweeps above are blind to that - they only ever look
			// at the records carrying the field, where a perfectly one-sided field looks pure but tiny.
			Console.WriteLine("\nPresence sweep - is a field's mere PRESENCE the statement?\n");
			Console.WriteLine($"{"field",6} {"on pct",8} {"of",6} {"on flat",8} {"of",6}  {"one-sided coverage",18}");

			foreach (var x in perField
				         .Select(kv => (Field: kv.Key, P: kv.Value.Sum(v => v.Value.Percent), F: kv.Value.Sum(v => v.Value.Flat)))
				         // How much of ONE class it covers while staying out of the other - 1.0 would be the flag.
				         .Select(x => (x.Field, x.P, x.F, Score: Math.Max(x.P == 0 ? 0 : (double)x.F / flats * (x.P == 0 ? 1 : 0), x.F == 0 ? (double)x.P / percents : 0)))
				         .OrderByDescending(x => x.Score)
				         .ThenByDescending(x => x.P + x.F)
				         .Take(12))
				Console.WriteLine($"{x.Field,6} {x.P,8} {percents,6} {x.F,8} {flats,6}  {x.Score,18:P2}");

			// Fourth shape: the record's fixed HEAD, at word granularity, bit by bit. Same test as the field bit
			// sweep, over the region the property reader never reaches.
			Console.WriteLine($"\nHead-word bit sweep - the first {HeadWords} words of the record body:\n");
			Console.WriteLine($"{"word",5} {"@byte",6} {"bit",4} {"agree",8}  set:pct/flat  clear:pct/flat");

			var headBits = new List<(int Word, int Bit, double Agreement, int SetP, int SetF, int ClearP, int ClearF)>();
			foreach (var (word, byValue) in headWords)
			{
				for (var bit = 0; bit < 32; bit++)
				{
					int setP = 0, setF = 0, clearP = 0, clearF = 0;
					foreach (var (value, (p, f)) in byValue)
					{
						if ((value >> bit & 1) == 1)
						{
							setP += p;
							setF += f;
						}
						else
						{
							clearP += p;
							clearF += f;
						}
					}

					if (setP + setF == 0 || clearP + clearF == 0)
						continue;

					headBits.Add((word, bit, Math.Max(setP + clearF, setF + clearP) / (double)(percents + flats), setP, setF, clearP, clearF));
				}
			}

			foreach (var b in headBits.OrderByDescending(x => x.Agreement).Take(15))
				Console.WriteLine($"{b.Word,5} {b.Word * 4,6} {b.Bit,4} {b.Agreement,8:P2}  {b.SetP,5}/{b.SetF,-5} {b.ClearP,7}/{b.ClearF,-5}");

			// The answer is word 13 bit 2 (see SpellFlagsWordOffset). Where it and the magnitude heuristic DISAGREE is
			// the only interesting set left, because one of them is wrong there - so list it rather than summarising
			// it away. Every disagreement is the same direction: the heuristic says percent, the flag says flat.
			Console.WriteLine($"\nWhere the flag (word {SpellFlagsWordOffset / 4} bit 2) and the <= {threshold} heuristic disagree:\n");
			var disagreements = new List<(uint Id, string Name, uint Cost)>();
			foreach (var e in rdb.EntriesOfType(SpellTemplateType))
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
					continue;
				}

				if (body.Length < SpellFlagsWordOffset + 4 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				var cost = RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12)).GetValueOrDefault(SpellManaCostField);
				if (cost == 0)
					continue;

				var flagSaysPercent = (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(SpellFlagsWordOffset, 4)) >> 2 & 1) == 1;
				if (flagSaysPercent == cost <= threshold)
					continue;

				var (name, _) = RdbFeatImporter.ReadNameAndDescription(body);
				disagreements.Add((e.Id, name, cost));
			}

			Console.WriteLine($"{disagreements.Count} of {percents + flats} records disagree; costs {string.Join(", ", disagreements.Select(d => d.Cost).Distinct().Order().Take(20))}");
			foreach (var d in disagreements.OrderBy(d => d.Cost).Take(15))
				Console.WriteLine($"  {d.Id,9} {d.Name,-44} {d.Cost}");
		}

		// ⚠️ A spell template's property table ASCENDS, where an item's strictly DESCENDS (RdbItemImporter's
		// TryReadPropertyField). Same [fieldId][value] grammar, opposite direction - reusing the item reader here
		// finds nothing at all.
		private static void SpellLineIds(string rdbDir, string? arg)
		{
			var wanted = uint.TryParse(arg, out var w) ? w : 0u;
			using var rdb = OpenRdb(rdbDir);
			var byLine = new SortedDictionary<uint, List<string>>();
			var scanned = 0;
			var carrying = 0;

			foreach (var e in rdb.EntriesOfType(SpellTemplateType))
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
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != SpellTemplateType)
					continue;

				scanned++;
				var line = RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12)).GetValueOrDefault(SpellLineField);
				if (line == 0)
					continue;

				carrying++;
				if (wanted != 0 && line != wanted)
					continue;

				var (name, _) = RdbFeatImporter.ReadNameAndDescription(body);
				byLine.TryAdd(line, []);
				byLine[line].Add($"{e.Id} {name}");
			}

			Console.WriteLine($"spell templates scanned {scanned}, carrying field {SpellLineField}: {carrying}");
			Console.WriteLine();
			foreach (var (line, names) in byLine)
			{
				var @class = line / 1000;
				var known = Enum.IsDefined(typeof(ConanClass), (ConanClass)@class) ? ((ConanClass)@class).ToString() : $"class {@class}?";
				Console.WriteLine($"  {line}  ({known}, line {line % 1000})  x{names.Count}");
				foreach (var n in names.Take(wanted != 0 ? 40 : 3))
					Console.WriteLine($"      {n}");
			}
		}

		// RDB 1040005 - the spell template type (feats/spells/items share one record layout).
		private const uint SpellTemplateType = 1040005;

		// Property-table field 128: the spell's LINE id, shared by all its ranks.
		private const uint SpellLineField = 128;

		// Property-table field 132: the mana cost, in whichever of its two modes the (undecoded) flag selects - see
		// SpellManaMode and RdbSpellImporter.ManaCostField.
		private const uint SpellManaCostField = 132;

		// How much of a spell record's fixed head SpellManaMode sweeps. The ascending property run starts around word
		// 15 of the body and is only ~16 fields wide, so everything before it is struct rather than property table.
		private const int HeadWords = 20;

		// Byte 52 of a spell record - word 13, the FLAGS word, and the one the client keeps at `spell + 0xC8` (#630).
		// Its bit 2 is what picks a percent-of-pool mana cost over a flat one.
		private const int SpellFlagsWordOffset = 52;

		// --report feat-table [--report-arg <value>]
		//
		// All TWELVE words of every entry in the client's feat table (RDB 1000042 id 1). The importer reads six of
		// them - (wire, template), the three prereq columns, the root flag and the base index - and #590 needs to know
		// what the other six hold, because a feat's modifier record references a target id in a ~20000-45000 space
		// that resolves against nothing we import. With `--report-arg <value>` it reports which COLUMN, if any, holds
		// that value - which is the whole question.
		private static void FeatTable(string rdbDir, string? arg)
		{
			var needle = uint.TryParse(arg, out var v) ? v : 0u;
			using var rdb = OpenRdb(rdbDir);
			var matches = rdb.EntriesOfType(1000042u).Where(e => e.Id == 1).ToList();
			if (matches.Count == 0)
			{
				Console.WriteLine("feat-table: RDB 1000042 id 1 not present");
				return;
			}

			var body = rdb.ReadDecompressed(matches[0]);
			var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
			Console.WriteLine($"feat table: {count} entries, {body.Length} bytes");

			const int header = 20, stride = 48, words = 12;
			var columnHits = new int[words];
			var perColumnRange = new (uint Min, uint Max)[words];
			for (var c = 0; c < words; c++)
				perColumnRange[c] = (uint.MaxValue, 0);

			var shown = 0;
			for (var i = 0; i < count; i++)
			{
				var at = header + (i * stride);
				if (at + stride > body.Length)
					break;

				var w = new uint[words];
				for (var c = 0; c < words; c++)
					w[c] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + (c * 4)));

				for (var c = 0; c < words; c++)
				{
					if (w[c] == 0)
						continue;

					perColumnRange[c] = (Math.Min(perColumnRange[c].Min, w[c]), Math.Max(perColumnRange[c].Max, w[c]));
					if (needle != 0 && w[c] == needle)
					{
						columnHits[c]++;
						if (shown++ < 12)
							Console.WriteLine($"  hit col[{c}] entry {i}: " + string.Join(' ', w.Select(x => x.ToString().PadLeft(9))));
					}
				}

				if (needle == 0 && i < 6)
					Console.WriteLine($"  entry {i}: " + string.Join(' ', w.Select(x => x.ToString().PadLeft(9))));
			}

			Console.WriteLine();
			Console.WriteLine("per-column non-zero range:");
			for (var c = 0; c < words; c++)
			{
				var known = c switch
				{
					0 => " (wire id)", 1 => " (template id)", 2 or 3 or 4 => " (prereq)", 8 => " (root flag)", 10 => " (base index)", _ => ""
				};
				var range = perColumnRange[c].Min == uint.MaxValue ? "all zero" : $"{perColumnRange[c].Min}..{perColumnRange[c].Max}";
				Console.WriteLine($"  col[{c,2}] {range}{known}" + (needle != 0 ? $"   hits={columnHits[c]}" : ""));
			}
		}

		// --report feat-spell-modifiers [--report-arg <kind>]
		//
		// #590/#591. Every feat template's modifier records whose payload's first word is OUTSIDE the stat-id range -
		// exactly the ones ItemTemplateStatModifiers.Parse drops on `statId >= 0x1000`. That bound is right for a STAT
		// modifier and wrong for whatever these are: `Decomposition` rank 5 carries (41000, 3500) under kind 53075,
		// and 3500 is the +35.00% its in-client tooltip shows.
		//
		// The KIND is a GameAction type id and the payload is that type's KEY LIST, one u32 per key
		// (docs/re/gameactionlist-grammar.md), so this prints each row as its named slots rather than as a pair -
		// `SpellStatModifier` puts the spell first and the STAT second, which read positionally looked like a
		// magnitude that varied for no reason.
		private static void FeatSpellModifiers(string rdbDir, string? arg)
		{
			var onlyKind = uint.TryParse(arg, out var k) ? k : 0u;
			using var rdb = OpenRdb(rdbDir);
			var byKind = new Dictionary<uint, int>();
			var samples = new Dictionary<uint, List<string>>();
			var refCounts = new Dictionary<uint, int>();
			var feats = 0;

			// A payload slot printed as `key=value`, with the client's own name for the key where it has one.
			static string Slots(ItemTemplateStatModifiers.Element e)
			{
				var type = e.Type;
				if (type == null)
					return string.Join(' ', e.Values.Select((v, i) => $"[{i}]={v}"));

				return string.Join(' ', e.Values.Select((v, i) =>
				{
					var key = type.Keys[i];
					var name = key switch
					{
						GameActionTypes.KeyStat => "Stat",
						GameActionTypes.KeyLine => "Line",
						GameActionTypes.KeyValue => "Value",
						GameActionTypes.KeyAmount => "Amount",
						GameActionTypes.KeySpell => "Spell",
						_ => $"k{key}"
					};
					return $"{name}={v}";
				}));
			}

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
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != FeatTemplateType)
					continue;

				// The out-of-stat-range half, kept whole rather than as a pair - the same elements ParseRejected
				// yields, with every slot the kind declares.
				var hits = ItemTemplateStatModifiers.ParseElements(body.AsSpan(12))
					.Where(x => x.Values.Length > 0 && x.Values[0] >= 0x1000)
					.ToList();
				if (hits.Count == 0)
					continue;

				feats++;
				var (name, description) = RdbFeatImporter.ReadNameAndDescription(body);
				foreach (var hit in hits)
				{
					var kind = hit.Kind;
					if (onlyKind != 0 && kind != onlyKind)
						continue;

					byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
					refCounts[hit.Values[0]] = refCounts.GetValueOrDefault(hit.Values[0]) + 1;
					samples.TryAdd(kind, []);
					// With a kind filter the caller is decoding THAT kind, so print every row and its description -
					// the description names the spell the feat modifies, which is the only handle we have on what the
					// reference id means. Unfiltered it is a census, so a few samples per kind is enough.
					if (onlyKind != 0 || samples[kind].Count < 6)
						samples[kind].Add($"{e.Id}\t{name}\t{Slots(hit)}\t{description.Replace('\t', ' ')}");
				}
			}

			Console.WriteLine($"feat templates carrying an out-of-stat-range modifier: {feats}");
			Console.WriteLine();
			Console.WriteLine("by KIND:");
			foreach (var (kind, n) in byKind.OrderByDescending(p => p.Value))
			{
				var type = GameActionTypes.Get(kind);
				Console.WriteLine($"  {kind} (0x{kind:X})  x{n}  {type?.Name ?? "<unregistered>"}");
				foreach (var line in samples[kind])
					Console.WriteLine($"      {line}");
			}

			Console.WriteLine();
			Console.WriteLine("most-referenced targets:");
			foreach (var (reference, n) in refCounts.OrderByDescending(p => p.Value).Take(20))
				Console.WriteLine($"  ref {reference} x{n}");
		}

		// --report feat-record --report-arg [<rdbType>/]<id>[,<id>...]
		//
		// The RAW words of one or more feat templates, side by side. Built for #590: a feat like `Decomposition`
		// carries "+35% to Flesh to Worms" that the stat-modifier region does not hold, so finding where the client
		// keeps it means diffing the five RANKS of one feat against each other - the words that MOVE across ranks are
		// the magnitude, the ones that stay are the target. Passing several ids prints a per-word diff column for
		// exactly that.
		private static void FeatRecord(string rdbDir, string? arg)
		{
			// An optional `<type>/` prefix dumps a record of ANOTHER RDB type with the same machinery - spell
			// templates (1040005) share the feat/item record layout, and #590 needs to compare the two.
			var spec = (arg ?? "").Split('/', 2, StringSplitOptions.TrimEntries);
			var type = spec.Length == 2 && uint.TryParse(spec[0], out var t) ? t : FeatTemplateType;
			var ids = spec[^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(x => uint.TryParse(x, out var v) ? v : 0).Where(v => v != 0).ToArray();
			if (ids.Length == 0)
			{
				Console.WriteLine("feat-record: pass one or more template ids, e.g. --report-arg 1173623,1173628");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			var bodies = new Dictionary<uint, byte[]>();
			foreach (var e in rdb.EntriesOfType(type))
			{
				if (e.Size == 0 || !ids.Contains(e.Id))
					continue;

				try
				{
					bodies[e.Id] = rdb.ReadDecompressed(e);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"  {e.Id}: unreadable ({ex.GetType().Name})");
				}
			}

			foreach (var id in ids)
			{
				if (!bodies.TryGetValue(id, out var body))
				{
					Console.WriteLine($"{id}: not present in RDB type {type}");
					continue;
				}

				var (name, description) = RdbFeatImporter.ReadNameAndDescription(body);
				var (start, count) = RdbFeatImporter.EffectTail(body);
				Console.WriteLine($"=== {id}  {body.Length} bytes  name=\"{name}\"  effects={count} (at {start})");
				Console.WriteLine($"    desc: {description}");
			}

			// A hex dump per record. The text is variable-length, so everything after it is NOT 4-aligned to the
			// record start - the word grid below will straddle the real fields, and this is what lets a reader
			// re-cut them by eye.
			foreach (var id in ids.Where(bodies.ContainsKey))
			{
				var body = bodies[id];
				Console.WriteLine();
				Console.WriteLine($"--- {id} hex");
				for (var off = 0; off < body.Length; off += 16)
				{
					var run = Math.Min(16, body.Length - off);
					var hex = string.Join(' ', Enumerable.Range(0, run).Select(k => body[off + k].ToString("x2")));
					var ascii = string.Concat(Enumerable.Range(0, run).Select(k => body[off + k] is >= 0x20 and < 0x7F ? (char)body[off + k] : '.'));
					Console.WriteLine($"  {off,4:d}  {hex.PadRight(47)}  {ascii}");
				}
			}

			// The word-by-word view. Only the words the records DISAGREE on are interesting when several are given,
			// so those are flagged - that is the whole point of passing a feat's five ranks at once.
			var present = ids.Where(bodies.ContainsKey).ToArray();
			if (present.Length == 0)
				return;

			var longest = present.Max(i => bodies[i].Length);
			Console.WriteLine();
			Console.WriteLine("offset   " + string.Join("  ", present.Select(i => i.ToString().PadLeft(10))) + "   diff");
			for (var off = 0; off + 4 <= longest; off += 4)
			{
				var words = present.Select(i => bodies[i].Length >= off + 4
					? BinaryPrimitives.ReadUInt32LittleEndian(bodies[i].AsSpan(off))
					: (uint?)null).ToArray();
				var differs = words.Distinct().Count() > 1;
				Console.WriteLine($"  [{off,4}]  "
					+ string.Join("  ", words.Select(w => (w?.ToString() ?? "-").PadLeft(10)))
					+ (differs ? "   <<<" : ""));
			}
		}

		private static void FeatEffectWidths(string rdbDir, string? arg)
		{
			var templateType = uint.TryParse(arg, out var t) && t != 0 ? t : FeatTemplateType;
			using var rdb = OpenRdb(rdbDir);
			var widths = new Dictionary<uint, Dictionary<int, int>>();
			var surplus = new Dictionary<uint, (uint Id, uint[] Words)>();
			var tiled = new Dictionary<uint, Dictionary<int, (int Count, uint First, uint[] Extra)>>();
			var regions = 0;
			var framed = 0;
			var frameAgrees = 0;
			var singles = 0;
			var multi = 0;

			foreach (var e in rdb.EntriesOfType(templateType))
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
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != templateType)
					continue;

				// ---- Measurement 2: TILING (#595) ----
				// The pass below derives a kind's width from records holding exactly ONE element, where the only
				// available end marker is the record's, so what it measures is a CEILING - it swallows whatever the
				// record carries after the element list. Elements sit back to back with nothing between them, so a
				// record holding SEVERAL measures the non-final ones EXACTLY: the distance to the next element's
				// opening type word IS that element's width. That is an independent measurement, and it is the one
				// that sees the `Swift Pathing` case the ceiling pass is structurally blind to (two elements, both
				// short, in a record with no single-element sibling).
				//
				// The final element is deliberately NOT tallied: its end is the record's, so it is a ceiling like the
				// pass below, and mixing the two would let trailing record content masquerade as element bytes.
				var located = ItemTemplateStatModifiers.ParseElements(body.AsSpan(12));
				if (located.Count > 0)
				{
					regions++;

					// The list frame sits immediately before the first element's type word - the same `0x3F1` count
					// word every other list in the protocol carries. Reading it here is what turns "the scan found N
					// signature hits" into "the record SAYS there are N", so a spurious hit shows up as disagreement
					// instead of quietly widening a histogram.
					var frameOff = 12 + located[0].Offset - 4;
					var declared = frameOff >= 0 && ListFrame.TryCountOf(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(frameOff)), out var c) ? c : -1;
					if (declared >= 0)
						framed++;

					// Tally only where the record's own count agrees with what the scan found. Where it does not, the
					// scan has either crossed out of the element region (into trailing record content that happens to
					// carry the signature) or missed one, and the distance between two "consecutive" elements is then
					// a GAP, not a width - which is where the non-multiple-of-4 "widths" (43B, 47B, 53B...) come
					// from. Agreement is the cheap, record-supplied proof that a pair really is adjacent.
					if (declared == located.Count)
					{
						frameAgrees++;
						for (var i = 0; i + 1 < located.Count; i++)
						{
							var w = located[i + 1].Offset - located[i].Offset;
							tiled.TryAdd(located[i].Kind, []);
							var seenSoFar = tiled[located[i].Kind];
							var prior = seenSoFar.GetValueOrDefault(w);

							// The words a measured element carries PAST its key list. Same reason the ceiling pass
							// prints them: "ChangeHitPercent disagrees" is a shrug, "ChangeHitPercent is followed by
							// four 0xFF words - the key list's own terminator value" is a lead.
							var want = GameActionTypes.Get(located[i].Kind) is { IsFixedWidth: true } ft ? 4 * ft.WidthWords : 0;
							var extra = prior.Count == 0 && want > 0 && w > want
								? Enumerable.Range(0, (w - want) / 4)
									.Select(k => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12 + located[i].Offset + want + 4 * k))).ToArray()
								: prior.Extra;
							seenSoFar[w] = (prior.Count + 1, prior.Count == 0 ? e.Id : prior.First, extra);
						}
					}
				}

				var (start, count) = RdbFeatImporter.EffectTail(body);
				if (count <= 0 || start + 4 > body.Length)
					continue;

				if (count > 1)
				{
					multi++;
					continue;
				}

				singles++;
				var kind = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(start));
				var width = body.Length - start;
				widths.TryAdd(kind, new Dictionary<int, int>());
				widths[kind][width] = widths[kind].GetValueOrDefault(width) + 1;

				// A measured width WIDER than the key list predicts is bytes past the element, and this is what says
				// WHAT they are rather than only that they exist - the difference between "AddSkinnedMesh disagrees"
				// and "AddSkinnedMesh is followed by `2 9 207912`". Both surpluses seen so far are trailing RECORD
				// content, not element bytes: `Modify`'s is a pair of list frames (0x7E2, 0x3F1) and
				// `SetMorphValue`'s is an ASCII mesh name.
				var fixedWidth = GameActionTypes.Get(kind) is { IsFixedWidth: true } fw ? fw : null;
				var predicted = 4 * (fixedWidth?.WidthWords ?? 0);
				if (predicted > 0 && width > predicted && !surplus.ContainsKey(kind))
					surplus[kind] = (e.Id, [.. Enumerable.Range(0, (width - predicted) / 4)
						.Select(i => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(start + predicted + 4 * i)))]);
			}

			Console.WriteLine($"single-element records: {singles}   multi-element: {multi}");
			Console.WriteLine("kind -> width(s) observed in single-element records, vs the width its KEY LIST predicts:");
			foreach (var (kind, seen) in widths.OrderByDescending(k => k.Value.Values.Sum()))
			{
				var type = GameActionTypes.Get(kind);
				var predicted = type == null ? "<unregistered>" : type.IsFixedWidth ? $"{4 * type.WidthWords}B {type.Name}" : $"VARIABLE (a text slot) {type.Name}";
				var agrees = type == null || !type.IsFixedWidth || seen.Keys.Contains(4 * type.WidthWords) ? "" : "   <-- DISAGREES";
				Console.WriteLine($"  0x{kind:X4}  {string.Join(", ", seen.OrderByDescending(w => w.Value).Select(w => $"{w.Key}B x{w.Value}"))}   predicted {predicted}{agrees}");
				if (surplus.TryGetValue(kind, out var extra))
					Console.WriteLine($"            surplus in {extra.Id}: {string.Join(" ", extra.Words.Select(w => $"{w} (0x{w:X})"))}");
			}

			Console.WriteLine();
			Console.WriteLine($"element regions: {regions}   with a readable 0x3F1 frame: {framed}   whose count matches the elements found: {frameAgrees}");
			Console.WriteLine("kind -> width(s) MEASURED by tiling (an element's start to the NEXT element's start), vs the width its KEY LIST predicts:");
			foreach (var (kind, seen) in tiled.OrderByDescending(k => k.Value.Values.Sum(v => v.Count)))
			{
				var type = GameActionTypes.Get(kind);
				var predicted = type == null ? "<unregistered>" : type.IsFixedWidth ? $"{4 * type.WidthWords}B {type.Name}" : $"VARIABLE (a text slot) {type.Name}";
				var agrees = type == null || !type.IsFixedWidth || seen.Keys.All(w => w == 4 * type.WidthWords) ? "" : "   <-- DISAGREES";
				Console.WriteLine($"  0x{kind:X4}  {string.Join(", ", seen.OrderByDescending(w => w.Value.Count).Select(w => $"{w.Key}B x{w.Value.Count} (e.g. {w.Value.First})"))}   predicted {predicted}{agrees}");
				foreach (var (width, hit) in seen.Where(w => w.Value.Extra is { Length: > 0 }))
					Console.WriteLine($"            {width}B carries {hit.Extra.Length} word(s) past the key list, in {hit.First}: {string.Join(" ", hit.Extra.Select(w => $"{w} (0x{w:X})"))}");
			}
		}

		// --report feat-effect-kinds [--report-arg <kind>]
		//
		// Every effect KIND the feat catalog actually carries, with the key list the client registers for it, and -
		// the point of the report - what the two readings of the same element DISAGREE about (Gitea #597).
		//
		// `ItemTemplateStatModifiers.Modifier` is the PAIR view: payload words 0 and 1, whatever they mean. That is
		// (stat, value) for `Modify` and for nothing else in particular - `InterpolatedModify` puts a Substate in word
		// 1 and its Amount in word 3, `TargetSpellLineStatModifier` puts a spell LINE in word 0 and the stat in word
		// 1, `AddPercentSkillModifier` has no stat slot at all. So the report prints, per kind, the pair a positional
		// read yields beside the values the KEY-addressed read yields for the same element, and flags every kind where
		// they differ. A kind whose stat/amount keys are not in slots 0/1 is one the pair view cannot import.
		private static void FeatEffectKinds(string rdbDir, string? arg)
		{
			// `<type>/<kind>` - the same spelling `feat-record` uses. Spell templates (1040005) share the record
			// layout and the same region parser, so the same census answers "does this kind read the same way for a
			// spell", which is what ties a decode to the in-client tooltips we have for spells.
			var spec = (arg ?? "").Split('/', 2, StringSplitOptions.TrimEntries);
			var templateType = spec.Length == 2 && uint.TryParse(spec[0], out var t) ? t : FeatTemplateType;
			var only = uint.TryParse(spec[^1], out var k) ? k : 0u;
			using var rdb = OpenRdb(rdbDir);

			// Per kind: how many elements carry it, one sample (feat id + name + the whole payload), and whether the
			// pair view and the key-addressed view agree on that sample.
			var seen = new Dictionary<uint, (int Count, uint FeatId, string FeatName, ItemTemplateStatModifiers.Element Sample)>();
			// Every distinct payload of the FILTERED kind, so a single-kind run reads as a census of that kind's
			// slots rather than one sample - which is what makes "slot 1 tracks the tooltip, slot 3 does not"
			// checkable instead of asserted.
			var samples = new List<(uint Id, string Name, ItemTemplateStatModifiers.Element Element, string Raw)>();
			var textAudit = new Dictionary<string, List<(uint Id, string Name, uint Kind, string Bytes)>>();

			foreach (var e in rdb.EntriesOfType(templateType))
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
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != templateType)
					continue;

				var name = RdbFeatImporter.ReadNameAndDescription(body).Name;
				var located = ItemTemplateStatModifiers.ParseElements(body.AsSpan(12));
				for (var i = 0; i < located.Count; i++)
				{
					// A text-carrying element whose successor is known: its measured width settles the length word.
					if (located[i].Type is not { IsFixedWidth: false } tt || i + 1 >= located.Count)
						continue;

					var measured = located[i + 1].Offset - located[i].Offset;
					var texts = tt.Keys.Count(k => GameActionTypes.TextKeys.Contains(k));
					var fixedSlots = tt.Keys.Length - texts;
					// 32-byte prefix + one u32 per non-text slot + per text slot either [u32 len][len bytes] or
					// [u16 len][len bytes]. The string BYTES are the same either way, so the two forms differ only by
					// 2 bytes per text slot - which is exactly what the measured width can tell apart.
					var bytes = measured - 32 - 4 * fixedSlots;
					var slotStart = 12 + located[i].Offset + 32 + 4 * tt.Keys.TakeWhile(k => !GameActionTypes.TextKeys.Contains(k)).Count();
					var raw = string.Join(" ", Enumerable.Range(0, Math.Min(24, body.Length - slotStart)).Select(k => body[slotStart + k].ToString("x2")));
					var u32Len = texts == 1 && slotStart + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(slotStart)) : uint.MaxValue;
					var u16Len = texts == 1 && slotStart + 2 <= body.Length ? BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(slotStart)) : ushort.MaxValue;
					var form = texts != 1
						? $"{texts} text slots - not decided"
						: bytes == 4 + u32Len
							? "[u32 len][bytes]"
							: bytes == 2 + u16Len
								? "[u16 len][bytes]"
								: "NEITHER";
					textAudit.TryAdd(form, []);
					textAudit[form].Add((e.Id, name, located[i].Kind, raw));
				}

				foreach (var element in located)
				{
					if (only != 0 && element.Kind != only)
						continue;

					var prior = seen.GetValueOrDefault(element.Kind);
					seen[element.Kind] = prior.Count == 0
						? (1, e.Id, name, element)
						: (prior.Count + 1, prior.FeatId, prior.FeatName, prior.Sample);
					if (only != 0)
					{
						// The payload BYTES, from the four always-present values onward. An UNREGISTERED kind has no
						// key list, so the key=value rendering below invents two slots for it and shows nothing else -
						// the bytes are the only way to see what such a record actually holds (Gitea #596).
						var from = 12 + element.Offset + 24;
						samples.Add((e.Id, name, element,
							string.Join(" ", Enumerable.Range(0, Math.Clamp(body.Length - from, 0, 48)).Select(k => body[from + k].ToString("x2")))));
					}
				}
			}

			Console.WriteLine("kind -> key list, and what the POSITIONAL pair view reads vs the KEY-addressed one.");
			Console.WriteLine("A kind whose Stat (key 0) is not payload word 0, or whose magnitude key is not word 1, is MISREAD by the pair view.");
			Console.WriteLine();

			foreach (var (kind, hit) in seen.OrderByDescending(s => s.Value.Count))
			{
				var type = GameActionTypes.Get(kind);
				var keys = type == null ? "<unregistered>" : $"[{string.Join(", ", type.Keys)}]";
				Console.WriteLine($"  {kind}  x{hit.Count,-6} {type?.Name ?? "<unregistered>",-32} keys {keys}");

				var statSlot = type?.SlotOf(GameActionTypes.KeyStat) ?? -1;
				var valueSlot = type?.SlotOf(GameActionTypes.KeyValue) ?? -1;
				var amountSlot = type?.SlotOf(GameActionTypes.KeyAmount) ?? -1;
				var magnitudeSlot = amountSlot >= 0 ? amountSlot : valueSlot;
				var verdict = statSlot == 0 && magnitudeSlot == 1
					? "pair view OK (Stat @0, magnitude @1)"
					: $"MISREAD: Stat @{(statSlot < 0 ? "none" : statSlot.ToString())}, magnitude @{(magnitudeSlot < 0 ? "none" : magnitudeSlot.ToString())}";
				Console.WriteLine($"           {verdict}");

				Console.WriteLine($"           e.g. feat {hit.FeatId} \"{hit.FeatName}\": {Payload(hit.Sample)}  ({hit.Sample.WidthBytes}B)");
				Console.WriteLine($"           pair view would import (StatId={(hit.Sample.Values.Length > 0 ? hit.Sample.Values[0] : 0)}, "
					+ $"Value={(hit.Sample.Values.Length > 1 ? hit.Sample.Values[1] : 0)})");
			}

			// The TEXT-slot audit (#596). A text slot is `[length][bytes]`, and which width that length word is has to
			// be MEASURED, not assumed: an element's true width is the distance to the next element's type word, so
			// the reading whose payload ends exactly there is the right one. Printed as a tally per (kind, width form)
			// with the raw bytes of the first disagreement, because "some records read as u16" is a shrug and "kind
			// 53261 reads `12 00 'Hash=WMTW;Level=40'` and tiles at 56B" is a lead.
			if (textAudit.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("--- text slots, by which length word makes the element tile to its measured width:");
				foreach (var (form, hits) in textAudit.OrderByDescending(a => a.Value.Count))
				{
					Console.WriteLine($"  {form} x{hits.Count}");
					foreach (var byKind in hits.GroupBy(h => h.Kind).OrderByDescending(g => g.Count()))
					{
						Console.WriteLine($"      kind {byKind.Key} {GameActionTypes.Get(byKind.Key)?.Name ?? "<unregistered>",-36} x{byKind.Count()}");
						// Up to eight members, not one. A length word is settled by the records that DISAGREE with the
						// rule, and a bucket printed as a single example cannot show whether its members agree with
						// each other - which is the whole question when a form holds six records (Gitea #596).
						foreach (var hit in byKind.Take(8))
							Console.WriteLine($"          {hit.Id,10} \"{hit.Name}\": {hit.Bytes}");
						if (byKind.Count() > 8)
							Console.WriteLine($"          ... and {byKind.Count() - 8} more");
					}
				}
			}

			if (samples.Count == 0)
				return;

			var only_ = GameActionTypes.Get(only);
			Console.WriteLine();
			Console.WriteLine($"--- every {only} ({only_?.Name ?? "<unregistered>"}) element, by key:");
			foreach (var (id, name, element, raw) in samples.OrderBy(s => s.Name).ThenBy(s => s.Id))
			{
				Console.WriteLine($"  {id,10} {name,-44} {Payload(element)}  ({element.WidthBytes}B)");
				Console.WriteLine($"             payload {raw}");
			}

			return;

			// One element as `key=value` pairs, in key order. A TEXT slot prints the STRING rather than its byte
			// length, which is what makes a string-aware read visible in the output at all.
			static string Payload(ItemTemplateStatModifiers.Element element)
			{
				return element.Type is not { } t
					? string.Join(" ", element.Values.Select((v, i) => $"[{i}]={v}"))
					: string.Join(" ", t.Keys.Select((key, i) => i >= element.Values.Length
						? $"{key}=-"
						: element.Texts[i] is { } text
							? $"{key}=\"{text}\""
							: $"{key}={element.Values[i]}"));
			}
		}

		// Classify the RDB feat/ability catalog (type 1000020) into PLAINTEXT vs TEA-ENCRYPTED records, and dump the
		// plaintext ones (Gitea #251). A plaintext record opens with its own TYPE word (1000020 = 0x000F4254, which
		// renders as `TB\x0f\x00` in a dump and is NOT a magic) while an encrypted one opens with `RDBH`.
		// The plaintext share needs NO key, so it is a feat catalog we can build today.
		// --report-arg <substring>  filters the dumped names   --out <dir>  writes feat-catalog.tsv
		private static void FeatCatalog(string rdbDir, string? filter, string? outDir)
		{
			using var rdb = OpenRdb(rdbDir);
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

				if (BinaryPrimitives.ReadUInt32LittleEndian(body) != FeatTemplateType)
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



		private static void RdbId(string rdbDir, string arg)
		{
			if (!uint.TryParse(arg, out var id))
			{
				Console.WriteLine($"rdb-id needs a numeric --report-arg, got '{arg}'");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
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
			using var rdb = OpenRdb(rdbDir);
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
			using var rdb = OpenRdb(rdbDir);
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
			using var rdb = OpenRdb(rdbDir);
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
			using var rdb = OpenRdb(rdbDir);
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
			using var rdb = OpenRdb(rdbDir);
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
			using var rdb = OpenRdb(rdbDir);
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
		// Every stat id the item [2][9] stat-modifier region carries, with how many templates carry it and its value
		// range - the whole region, NOT just RdbItemImporter's whitelist. The question it answers is the one that
		// keeps coming up when a consumer lands: "is this rating actually ON any item, or would importing it be dead
		// weight?" (#401 asked it of BonusArmor/BonusProtection.)
		private static void RegionStatCensus(string rdbDir)
		{
			using var db = OpenDb();
			var items = db.Items.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0).OrderBy(i => i.Id).ToList();
			using var rdb = OpenRdb(rdbDir);

			var counts = new Dictionary<uint, (int Items, long Min, long Max)>();
			var scanned = 0;
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
						// a wrong/stale key - skipped, like the sibling reports
					}
				}

				if (template is not { Length: >= 16 })
					continue;

				scanned++;
				foreach (var m in ItemTemplateStatModifiers.Parse(template.AsSpan(12)))
				{
					var amount = (long)m.WireAmount;
					counts[m.StatId] = counts.TryGetValue(m.StatId, out var prev)
						? (prev.Items + 1, Math.Min(prev.Min, amount), Math.Max(prev.Max, amount))
						: (1, amount, amount);
				}
			}

			Console.WriteLine($"region-stat-census: {scanned} templates read, {counts.Count} distinct stat ids");
			Console.WriteLine($"{"statId",-8} {"name",-32} {"items",7} {"min",12} {"max",12}");
			foreach (var (statId, row) in counts.OrderByDescending(k => k.Value.Items))
			{
				var name = Enum.IsDefined(typeof(CharacterStatId), statId)
					? ((CharacterStatId)statId).ToString()
					: "";
				Console.WriteLine($"{statId,-8} {name,-32} {row.Items,7} {row.Min,12} {row.Max,12}");
			}
		}

		private static void ItemStatScaling(string rdbDir, string? outPath)
		{
			using var db = OpenDb();
			var items = db.Items.Include(i => i.StatModifiers)
				.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0)
				.OrderBy(i => i.Id)
				.ToList();

			using var rdb = OpenRdb(rdbDir);
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
			using var rdb = OpenRdb(rdbDir);
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

		// The decompressed body of a per-map RDB resource, or empty when the map has no readable entry of that type.
		// `CanRead` rather than `IsLocal`: the heightmap/collision types are majority CDN-only (1070003 is 276 of 520),
		// and RdbReader pulls those through the mirror - gating on IsLocal dropped over half the world.
		private static byte[] ReadResource(RdbReader rdb, uint type, uint mapId)
		{
			if (!rdb.TryGet(type, mapId, out var e) || !rdb.CanRead(e))
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
					if (!p.IsDownload || p.SubOpcode != FullPlayerCharacterCodec.Opcode)
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
					$"{cap[..Math.Min(46, cap.Length)],-46} size={body.Length,8} instance=0x{instance:X8} 0x{FullPlayerCharacterCodec.Opcode:X8}@[{string.Join(",", positions)}]");
				Console.WriteLine($"    first48: {Convert.ToHexString(body.AsSpan(0, Math.Min(48, body.Length)))}");

				try
				{
					var rootMsg = SnapshotRoot.Of(body.AsSpan(4)); // past the record's carrier length word
					Console.WriteLine($"    inventory-root: OK ({rootMsg.Inventories.Count} containers)");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"    inventory-root: FAIL - {ex.GetType().Name}: {ex.Message}");
				}
			}
		}

		// The spell records' (type 1040005) property table, three ways:
		//   --report-arg <decimal id>     one spell's whole field table
		//   --report-arg field:<n>        every spell CARRYING field n, with its name and the value - the view that
		//                                 says whether an unread field is worth importing (#633)
		//   (no arg)                      a histogram of how often each field id appears, with samples
		//
		// Written for #274 (find the DURATION field) and kept because every later field decode has needed one of the
		// three - a field is only decodable once you can see WHICH spells carry it, which the histogram cannot say.
		private static void SpellFields(string rdbDir, string? arg)
		{
			using var rdb = OpenRdb(rdbDir);
			var counts = new Dictionary<uint, int>();
			var samples = new Dictionary<uint, List<uint>>();
			uint? byField = arg != null && arg.StartsWith("field:", StringComparison.OrdinalIgnoreCase) && uint.TryParse(arg[6..], out var f)
				? f
				: null;
			var carriers = new List<(uint Id, string Name, uint Value)>();
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

				if (byField is { } want && fields.TryGetValue(want, out var value))
					carriers.Add((e.Id, name, value));

				foreach (var kv in fields)
				{
					counts[kv.Key] = counts.GetValueOrDefault(kv.Key) + 1;
					if (!samples.TryGetValue(kv.Key, out var l))
						samples[kv.Key] = l = [];
					if (l.Count < 8)
						l.Add(kv.Value);
				}
			}

			if (byField is { } field)
			{
				var fieldName = Enum.GetName(typeof(CharacterStatId), field) ?? "(unnamed)";
				Console.WriteLine($"field {field} (0x{field:X3}) {fieldName}: {carriers.Count} spell record(s)");
				foreach (var c in carriers.OrderBy(c => c.Value).ThenBy(c => c.Name, StringComparer.Ordinal))
					Console.WriteLine($"  {c.Value,10}  {c.Id,9}  {c.Name}");

				foreach (var g in carriers.GroupBy(c => c.Value).OrderByDescending(g => g.Count()).Take(15))
					Console.WriteLine($"  value {g.Key,10} x{g.Count()}");

				return;
			}

			if (wanted != null)
			{
				Console.WriteLine($"spell {wanted} not found / unnamed");
				return;
			}

			// Every field id, not a top-N: the tail is where the undecoded ones live. The name comes from the
			// client's own static stat-name table (CharacterStatId, `docs/re/aoc-client-stat-names.tsv`) - the
			// spell record's field ids ARE that namespace, which is what names the 0x300+ run outright.
			foreach (var kv in counts.OrderByDescending(k => k.Value))
			{
				var name = Enum.GetName(typeof(CharacterStatId), kv.Key) ?? "";
				Console.WriteLine($"0x{kv.Key:X3} ({kv.Key,4}) count={kv.Value,5}  {name,-38} samples={string.Join(",", samples[kv.Key])}");
			}
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
					if (p.WireOpcode != op)
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
			using var rdb = OpenRdb(rdbDir);
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

		// Re-run the NpcSpawnArea creature resolver (#363/#489). The resolver resets its own machine assignments and
		// re-derives every tier, so this is idempotent and is how a change to the resolution RULES reaches the
		// committed faolan.db without a full RDB spawn re-import. Manual assignments (`.setnpcgroup`) are never touched.
		//
		// It needs the RDB only for the groups' wander polylines, which the containment tier uses as its membership
		// test (#491) and which are deliberately not stored in the DB. Running without them would silently apply
		// WEAKER rules than the importer and produce a different faolan.db, so the RDB is required rather than
		// optional. Run:
		//   dotnet run --project Faolan.Importer -- --report resolve-spawn-areas --Content:RdbDir <dir>
		private static async Task ResolveSpawnAreas(string rdbDir)
		{
			await using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);
			var roamPaths = new Dictionary<uint, IReadOnlyList<IReadOnlyList<Vector3>>>();
			foreach (var mapId in await db.Maps.Select(m => m.Id).ToListAsync())
			{
				if (!rdb.TryGet(NpcSpawnLayout.ResourceType, mapId, out var e) || !rdb.CanRead(e))
					continue;
				if (!NpcSpawnLayout.TryParse(rdb.ReadDecompressed(e), out var layout) || layout == null)
					continue;

				for (var gi = 0; gi < layout.Groups.Count; gi++)
					if (layout.Groups[gi].RoamPaths.Count > 0)
						roamPaths[NpcSpawnArea.ComputeId(mapId, gi)] = layout.Groups[gi].RoamPaths;
			}

			Console.WriteLine($"roam paths loaded for {roamPaths.Count} areas");

			async Task<string> Tally()
			{
				var rows = await db
					.NpcSpawnAreas.GroupBy(a => a.ResolvedBy)
					.Select(g => new
					{
						Tier = g.Key,
						Areas = g.Count(),
						Pop = g.Sum(a => a.MaxCount),
						Creatures = g.Select(a => a.NpcTemplateId).Distinct().Count()
					})
					.ToListAsync();
				return string.Join("\n", rows.OrderBy(r => r.Tier).Select(r => $"    {r.Tier,-14} areas={r.Areas,5}  population={r.Pop,6}  distinct creatures={r.Creatures}"));
			}

			Console.WriteLine("before:\n" + await Tally());
			var stats = await SpawnAreaResolver.ResolveAsync(db, roamPaths, SpawnAreaResolver.LoadRaceNames(rdb));
			Console.WriteLine($"resolved: name={stats.Name} containment={stats.Containment} id-propagation={stats.IdProp} race-unique={stats.RaceUnique}");
			Console.WriteLine("after:\n" + await Tally());
		}

		// The playfield's world-unit width (1070003 <tilemap Width>), for the heightmap cell size. 0 if unavailable.
		private static uint TilemapExtent(RdbReader rdb, uint mapId)
		{
			if (!rdb.TryGet(1070003, mapId, out var e) || !rdb.CanRead(e))
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

		// `--report sql-seed [--report-arg <dir>]` (#411): the dialect-neutral SQL seed - the game DATA as ONE file
		// that loads into SQLite and Postgres alike. Writes to the repo root by default, which is where faolan.sql is
		// committed.
		private static async Task<int> SqlSeed(string? outputDir)
		{
			var directory = outputDir ?? ".";
			await using var db = OpenDb();
			var body = Path.Combine(directory, "faolan.sql");
			var (tables, rows) = SqlSeedWriter.Write(db, body);
			Console.WriteLine($"wrote {body} ({tables} tables, {rows} rows)");
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
					baseBody = WorldObjectCodec.Build(row, DiagnosticHandle);
					baseOk = true;
				}
				catch (InvalidOperationException)
				{
					// Un-lifted row - this diagnostic REPORTS buildability per row, so catch the fail-fast throw and
					// record it as a failure instead of aborting the sweep.
				}

				var rewriteOk = baseOk && SpawnTransformPatch.TryRewritePlayfield(baseBody!, 0x01EA985Cu);
				Console.WriteLine($"map {row.MapId} {row.Name} baseBuild={baseOk} rewrite={rewriteOk} asset={row.GeometryAssetId}/{row.GeometryAssetType} words={row.Words.Count}");
				if (baseOk)
					Console.WriteLine("  BUILT: " + Convert.ToHexString(baseBody!).ToLowerInvariant());
			}
		}
	}
}
