using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
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

			if (report == "ldb-search")
			{
				LdbSearch(RdbDir(), arg ?? "");
				return 0;
			}

			// RDB + DB, no corpus: the ambient-bark catalog's readable set (#331).
			if (report == "npc-chatter")
			{
				await NpcChatter(RdbDir());
				return 0;
			}

			// Corpus-only: pair every captured fall with the impact SPEED the client reported for it (#515).
			if (report == "fall-speed")
			{
				FallSpeed(NamedCapture.LoadAll(capturesDir));
				return 0;
			}

			// RDB-only, no corpus: the client's archetype -> class membership (RDB 1000506), checked against the
			// hard-coded ConanArchetypes (#469).
			if (report == "class-hierarchy")
			{
				ClassHierarchy(RdbDir());
				return 0;
			}

			// RDB-only, no corpus: the Khitai faction & reputation catalog (#310) - RDB 1010701's relation matrix
			// joined to LDB[254]'s names and rank ladders.
			if (report == "factions")
			{
				Factions(RdbDir());
				return 0;
			}

			// RDB (LDB 51000) + DB, no corpus: can the client name the creature behind an unresolved `Name`-category
			// spawn group? (#684 - the group->creature link is recoverable statically; the STATS are not.)
			if (report == "name-group-templates")
			{
				await NameGroupTemplates(RdbDir());
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

			// RDB only, no corpus: #464's one entry whose evidence was never the capture corpus - the two u32s after
			// an FCTX texture's magic, censused against the header fields around them.
			if (report == "fctx-header")
			{
				FctxHeaderCensus(RdbDir(), arg);
				return 0;
			}

			// RDB only, no corpus: a spell's in-client TOOLTIP rendered out of our own decode, chain and all - the
			// end-to-end gate on the spell record (#775). --report-arg <spellId>[,<id>...][@<casterLevel>].
			if (report == "spell-tooltip")
			{
				SpellTooltip(RdbDir(), arg);
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

				// #643: is the graph fold append-stable? Folds the corpus, then folds it again with each capture held
				// out, and counts the node ids that MOVE when it is added back. The measurement that ticket asks for
				// before its option (b) can be believed.
				case "conversation-fold-stability":
				{
					ConversationFoldStability(NamedCapture.LoadAll(capturesDir));
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

				case "morph-stats":
				{
					MorphStatsReport(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "stat-series":
				{
					StatSeries(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "weapon-set":
				{
					WeaponSetReport(NamedCapture.LoadAll(capturesDir));
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

				// #809's generalisation: every codec against every captured record of the type it declares, over all
				// the dumps. Parse then re-Build, byte-compared.
				case "codec-sweep":
				{
					CodecSweep(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				// #781: every death record's `field12` against the creature that died - is the animation's length the
				// CREATURE's, or the animation's?
				case "death-timing":
				{
					DeathTiming(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				// What happens AFTER the death record: when the loot bag lands and where, how long the body lies there,
				// how long an unlooted bag lasts, and whether the creature is ever seen to respawn.
				case "corpse-timing":
				{
					CorpseTiming(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				// #855: WHICH property of a creature picks its respawn interval - 120 s or 150 s. corpse-timing
				// established that retail respawns at all; this one carries the attributes and cross-tabs them.
				case "respawn-interval":
				{
					RespawnInterval(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				// #588/#400: does a captured spawn's worn list state which weapon SET is drawn?
				case "weapon-set-drawn":
				{
					WeaponSetDrawn(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "follow-pairs":
				{
					FollowPairs(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "bcc-attack-tail":
				{
					BccAttackTail(NamedCapture.LoadAll(capturesDir));
					return 0;
				}

				case "ambient-animation":
				{
					AmbientAnimation(NamedCapture.LoadAll(capturesDir), arg);
					return 0;
				}

				case "npc-pose-check":
				{
					return await NpcPoseCheck(NamedCapture.LoadAll(capturesDir));
				}

				case "item-level-check":
				{
					return await ItemLevelCheck(NamedCapture.LoadAll(capturesDir));
				}

				case "buff-duration-check":
				{
					return await BuffDurationCheck(NamedCapture.LoadAll(capturesDir));
				}

				case "spell-level-ladder":
				{
					return await SpellLevelLadder(arg);
				}

				case "spell-grant-check":
				{
					return await SpellGrantCheck(arg);
				}

				case "broadcast-check":
				{
					return BroadcastCheck(NamedCapture.LoadAll(capturesDir));
				}

				case "disposition-rule":
				{
					return await DispositionRule(NamedCapture.LoadAll(capturesDir));
				}

				case "spawn-animation":
				{
					SpawnAnimation(NamedCapture.LoadAll(capturesDir), _options.Configuration[Keys.ContentRdbDir]);
					return 0;
				}

				case "spawn-duplicates":
				{
					await SpawnDuplicates(NamedCapture.LoadAll(capturesDir), arg);
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

				case "ranged-standoff":
				{
					RangedStandoff(NamedCapture.LoadAll(capturesDir));
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

				case "chatter-groups":
				{
					ChatterGroups(RdbDir());
					return 0;
				}

				case "rdb-names":
				{
					RdbNames(RdbDir(), arg);
					return 0;
				}

				case "rdb-text-scan":
				{
					RdbTextScan(RdbDir(), arg);
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

				case "item-effect-kinds":
				{
					ItemEffectKinds(RdbDir(), arg);
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

				case "spell-element-lists":
				{
					SpellElementLists(RdbDir(), arg);
					return 0;
				}

				case "spell-flags":
				{
					await SpellFlags(RdbDir(), arg);
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

				case "spell-record-coverage":
				{
					await SpellRecordCoverage(RdbDir());
					return 0;
				}

				case "bcc-trees":
				{
					BccTrees(RdbDir());
					return 0;
				}

				case "data-table":
				{
					DataTable(RdbDir(), arg ?? "");
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
				case "ldb-name-categories":
				{
					await LdbNameCategories(RdbDir());
					return 0;
				}

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

		private static string Describe(RdbAssetExtractor.ResourceResult? r)
		{
			return r != null
				? $"id={r.Id} type={r.Type} {r.Availability} ({r.Size} B)"
				: "(none)";
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

		// Reports read the DB from the shared config (faolan.db by default, --Database:ConnectionString to override); one short-lived context.
		private static DatabaseContext OpenDb()
		{
			_db ??= CliConfig.ResolveDatabase(_options.Configuration);
			return DatabaseExtensions.CreateContext(_db.Value.Provider, _db.Value.Connection);
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
			return new RdbReader(rdbDir ?? RdbDir(), ReportRdbProxy());
		}

		// The CDN base a REPORT run reads through. ⚠️ Resolving it touches `_options`, which only the `--report` entry
		// point assigns - so anything shared with the IMPORT path must take the base as a parameter instead of calling
		// this (#819). It is a separate method rather than inline so that dependency is visible at each call site.
		private static string? ReportRdbProxy()
		{
			return _options.RdbProxy ?? _options.Configuration.CdnPublicAddress;
		}

		private static string RdbDir()
		{
			return _options.Configuration[Keys.ContentRdbDir] ??
				throw new InvalidOperationException(
					"The client's RDB folder is not configured - set Content:RdbDir in faolan.json, the Content__RdbDir environment variable, or pass --Content:RdbDir <dir>.");
		}

		// RDB 1040005 - the spell template type (feats/spells/items share one record layout).
		private const uint SpellTemplateType = 1040005;

		// How much of a spell record's fixed head SpellManaMode sweeps. The ascending property run starts around word
		// 15 of the body and is only ~16 fields wide, so everything before it is struct rather than property table.
		private const int HeadWords = 20;

		// One little-endian u32 of a record, or 0 past its end.
		private static uint Word(byte[] body, int offset)
		{
			return offset + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) : 0;
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

	}
}
