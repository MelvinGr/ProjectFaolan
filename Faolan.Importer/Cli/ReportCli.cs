using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;
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

		// One entry per report: its name, what it is for (the comment its `case` used to carry), whether it needs the
		// capture corpus, and the body. The table IS the index - `--report list` prints it, an unknown name answers
		// with the nearest ones, and nothing else has to be kept in step (#928 item 4; the old `default:` named 12
		// of ~166 reports and had drifted).
		internal enum Needs
		{
			None,
			Captures
		}

		internal sealed record ReportSpec(string Name, string Description, Needs Needs, Func<ReportContext, Task<int>> Run);

		// What a report body reads. `Captures` loads the corpus once, on first use - the 76 `NamedCapture.LoadAll`
		// calls the cases used to make each became this one property.
		internal sealed class ReportContext(ImporterOptions options)
		{
			private IReadOnlyList<NamedCapture>? _captures;
			public ImporterOptions Options { get; } = options;
			public string? Arg => Options.ReportArg;
			public string? Out => Options.Out;
			public string CapturesDir => Options.Captures;
			public IReadOnlyList<NamedCapture> Captures => _captures ??= NamedCapture.LoadAll(CapturesDir);
		}

		public static async Task<int> RunAsync(ImporterOptions options, string report)
		{
			_options = options; // OpenDb resolves the DB connection from these on demand (CliConfig)

			if (report == "list")
			{
				foreach (var s in Reports.OrderBy(s => s.Name, StringComparer.Ordinal))
					Console.WriteLine($"  {s.Name,-34} {(s.Needs == Needs.Captures ? "[captures] " : "")}{s.Description}");
				return 0;
			}

			var spec = Reports.FirstOrDefault(s => s.Name == report);
			if (spec == null)
			{
				var near = Reports.Select(s => s.Name)
					.Where(n => n.Contains(report, StringComparison.OrdinalIgnoreCase) || report.Contains(n, StringComparison.OrdinalIgnoreCase) || Levenshtein(n, report) <= 3)
					.OrderBy(n => Levenshtein(n, report)).Take(6).ToList();
				await Console.Error.WriteLineAsync($"Unknown report '{report}'." + (near.Count > 0
					? $" Did you mean: {string.Join(", ", near)}?"
					: "") + " `--report list` prints every report.");
				return 1;
			}

			var ctx = new ReportContext(options);
			if (spec.Needs == Needs.Captures && !Directory.Exists(ctx.CapturesDir))
			{
				await Console.Error.WriteLineAsync($"Captures directory not found: {ctx.CapturesDir}");
				await Console.Error.WriteLineAsync("  The corpus is NOT in this repo - it lives in the private ProjectFaolan-Data repo");
				await Console.Error.WriteLineAsync("  (see CLAUDE.md \"Capture data\"). Pass --captures <dir> pointing at its Captures/ folder.");
				return 1;
			}

			return await spec.Run(ctx);
		}

		private static int Levenshtein(string a, string b)
		{
			var d = new int[a.Length + 1, b.Length + 1];
			for (var i = 0; i <= a.Length; i++)
				d[i, 0] = i;
			for (var j = 0; j <= b.Length; j++)
				d[0, j] = j;
			for (var i = 1; i <= a.Length; i++)
			for (var j = 1; j <= b.Length; j++)
				d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
			return d[a.Length, b.Length];
		}

#pragma warning disable CS1998 // a report body without an await still runs through the one async signature
		internal static readonly ReportSpec[] Reports =

		[
			new("acg-token-check", "DB-only: how many minted ACGHash tokens collide with harvested ones, and the next clean salt (#729).", Needs.None, async ctx =>
			{
				return await AcgTokenCheck();
			}),
			new("sql-seed", "", Needs.None, async ctx =>
			{
				return await SqlSeed(ctx.Arg);
			}),
			new("apply-seed", "", Needs.None, async ctx =>
			{
				return ApplySeed();
			}),
			new("terrain-los", "", Needs.None, async ctx =>
			{
				await TerrainLos(ctx.Arg);
				return 0;
			}),
			new("spell-effect-gap", "DB-only, no corpus and no RDB: how much of the imported spell-effect table reaches a live engine (#701).", Needs.None, async ctx =>
			{
				await SpellEffectGap(ctx.Arg);
				return 0;
			}),
			new("ldb-stat-names", "RDB-only, no corpus: the client's stat-name table (LDB cat 615).", Needs.None, async ctx =>
			{
				LdbStatNames(RdbDir(), ctx.Out);
				return 0;
			}),
			new("ldb-search", "", Needs.None, async ctx =>
			{
				LdbSearch(RdbDir(), ctx.Arg ?? "");
				return 0;
			}),
			new("npc-chatter", "RDB + DB, no corpus: the ambient-bark catalog's readable set (#331).", Needs.None, async ctx =>
			{
				await NpcChatter(RdbDir());
				return 0;
			}),
			new("fall-speed", "Corpus-only: pair every captured fall with the impact SPEED the client reported for it (#515).", Needs.None, async ctx =>
			{
				FallSpeed(ctx.Captures);
				return 0;
			}),
			new("class-hierarchy", "RDB-only, no corpus: the client's archetype -> class membership (RDB 1000506), checked against the hard-coded ConanArchetypes (#469).", Needs.None, async ctx =>
			{
				ClassHierarchy(RdbDir());
				return 0;
			}),
			new("factions", "RDB-only, no corpus: the Khitai faction & reputation catalog (#310) - RDB 1010701's relation matrix joined to LDB[254]'s names and rank ladders.", Needs.None, async ctx =>
			{
				Factions(RdbDir());
				return 0;
			}),
			new("name-group-templates", "RDB (LDB 51000) + DB, no corpus: can the client name the creature behind an unresolved `Name`-category spawn group? (#684 - the group->creature link is recoverable statically; the STATS are not.)", Needs.None, async ctx =>
			{
				await NameGroupTemplates(RdbDir());
				return 0;
			}),
			new("bounded-areas", "RDB (+ DB for the cross-checks), no corpus: the bounded-area trigger-volume survey (#513).", Needs.None, async ctx =>
			{
				await BoundedAreas(RdbDir(), ctx.Arg, CapturedBanners(ctx.CapturesDir));
				return 0;
			}),
			new("ldb-quest-text", "RDB (ciphertext) + DB (the captured per-string keys), no corpus: the encrypted quest text (#520).", Needs.None, async ctx =>
			{
				await LdbQuestText(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("rdb-quest-catalog", "RDB + DB, no corpus: the client's own quest catalog, RDB type 1000104 (#569). The DEFINITION side of the quest system - every quest in the game, with the designers' names in plaintext. The DB half is the captured `JournalSequence`, which the step-order check scores the catalog's own row order against.", Needs.None, async ctx =>
			{
				await RdbQuestCatalog(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("professions", "RDB (plaintext LDB) + DB (the stat cores), no corpus: the NPC PROFESSION catalog - what each ProfessionName hash resolves to, grouped by the role tag beside it (#649).", Needs.None, async ctx =>
			{
				await Professions(RdbDir());
				return 0;
			}),
			new("quest-goal-targets", "RDB (plaintext LDB) + DB (the goal catalog), no corpus: what each goal's TRAILING typed reference resolves to, and whether that string can serve as the match key `TargetName` is (#521).", Needs.None, async ctx =>
			{
				await QuestGoalTargets(RdbDir());
				return 0;
			}),
			new("ldb-dialogue", "The NPC-dialogue half of the same encrypted set (categories 50000-50003); --report-arg filters by NPC name.", Needs.None, async ctx =>
			{
				await LdbDialogue(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("timeline", "ONE capture, told as a story: --report-arg <capture>.tshark.json. Reads that dump directly rather than the corpus, and wants the RDB only to decrypt the LDB text it quotes (it degrades without it).", Needs.None, async ctx =>
			{
				await Timeline(ctx.Arg ?? "", RdbDir());
				return 0;
			}),
			new("fctx-header", "RDB only, no corpus: #464's one entry whose evidence was never the capture corpus - the two u32s after an FCTX texture's magic, censused against the header fields around them.", Needs.None, async ctx =>
			{
				FctxHeaderCensus(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("spell-tooltip", "RDB only, no corpus: a spell's in-client TOOLTIP rendered out of our own decode, chain and all - the end-to-end gate on the spell record (#775). --report-arg <spellId>[,<id>...][@<casterLevel>].", Needs.None, async ctx =>
			{
				SpellTooltip(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("snapshot-eligibility", "DATABASE only, no corpus and no RDB: which quests can ride the login snapshot and which arm stops the rest (#389). It reads only what we SHIP, so it runs from a plain clone.", Needs.None, async ctx =>
			{
				return await SnapshotEligibility();
			}),
			new("ldb-decrypt", "One encrypted string, from a key pasted out of a capture: --report-arg <category>:<id>:<wire key>.", Needs.None, async ctx =>
			{
				LdbDecryptOne(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("fixture-repack", "Rewrites the fixture archive in the current container format, content unchanged. Reads no captures for the same reason `fixture-set` does not: the archive's content cannot be re-derived here (#555). It also replaces any capture NAME still stored as an entry's source by its opaque capture id (#465).", Needs.None, async ctx =>
			{
				FixtureArchiveWriter.Repack(Path.Combine("Faolan.Tests", "Assets", "fixtures.bin"));
				return 0;
			}),
			new("fixture-rename", "Re-keys ONE named fixture without touching its bytes (#465). Above the corpus check for the same reason fixture-set is: it reads no captures.", Needs.None, async ctx =>
			{
				var split = (ctx.Arg ?? "").IndexOf('=');
				if (split <= 0)
				{
					await Console.Error.WriteLineAsync("usage: --report fixture-rename --report-ctx.Arg <oldKey>=<newKey>");
					await Console.Error.WriteLineAsync("       e.g. golden/SpawnAssassinTemplate.bin=golden/SpawnAssassinTemplate.bin");
					return 1;
				}

				FixtureArchiveWriter.RenameNamed(Path.Combine("Faolan.Tests", "Assets", "fixtures.bin"), ctx.Arg![..split], ctx.Arg[(split + 1)..]);
				return 0;
			}),
			new("fixture-substitute", "Substitutes personal character names inside the archive's packet bytes by EQUAL-LENGTH pseudonyms (#465 option B). Reads no captures - it edits the committed archive in place, verifying the old names are gone. `--dry-run` reports what it would change and writes nothing. The pseudonyms are the caller's call and must match the real name's byte length; the three golden test literals move in the same change (see FixtureArchiveWriter.Substitute).", Needs.None, async ctx =>
			{
				var pairs = new List<(string Old, string New)>();
				foreach (var token in (ctx.Arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					var eq = token.IndexOf('=');
					if (eq <= 0 || eq == token.Length - 1)
					{
						await Console.Error.WriteLineAsync("usage: --report fixture-substitute --report-ctx.Arg <old>=<new>[,<old2>=<new2>...] [--dry-run]");
						await Console.Error.WriteLineAsync("       each <new> MUST have the same byte length as its <old> (e.g. OldName=NewNam1).");
						return 1;
					}

					pairs.Add((token[..eq], token[(eq + 1)..]));
				}

				if (pairs.Count == 0)
				{
					await Console.Error.WriteLineAsync("usage: --report fixture-substitute --report-ctx.Arg <old>=<new>[,...] [--dry-run]");
					return 1;
				}

				return FixtureArchiveWriter.Substitute(Path.Combine("Faolan.Tests", "Assets", "fixtures.bin"), pairs, ctx.Options.DryRun);
			}),
			new("fixture-set", "Adds or replaces ONE named fixture in the archive. Reads no captures, so it sits above the corpus check: the hand-carved fixtures cannot be re-derived from the corpus, which is exactly why this exists.", Needs.None, async ctx =>
			{
				var split = (ctx.Arg ?? "").IndexOf('=');
				if (split <= 0)
				{
					await Console.Error.WriteLineAsync("usage: --report fixture-set --report-ctx.Arg <key>=<file>   e.g. golden/InitialBurst.bin=/tmp/burst.bin");
					await Console.Error.WriteLineAsync("       --report fixture-set --report-ctx.Arg <key>=        removes it (an empty source, #555)");
					return 1;
				}

				var archive = Path.Combine("Faolan.Tests", "Assets", "fixtures.bin");
				var source = ctx.Arg![(split + 1)..];

				// An empty source REMOVES the fixture - the path by which retail bytes leave the archive once a gate's
				// expectation has become a digest (#555). Spelled as the same flag rather than a second report so the
				// two halves of "what is in the archive" stay in one place.
				if (source.Length == 0)
					FixtureArchiveWriter.RemoveNamed(archive, ctx.Arg[..split]);
				else
					FixtureArchiveWriter.SetNamed(archive, ctx.Arg[..split], source);

				return 0;
			}),
			new("fixtures", "", Needs.Captures, async ctx =>
			{
				// The single file holding every byte the test suite reads: the corpus slice (rebuilt here) plus the
				// hand-carved named fixtures (carried over from the existing archive). See FixtureArchiveWriter.
				// Default paths here are CWD-relative: the importer runs from the repo root (see CLAUDE.md).
				var target = ctx.Arg ?? Path.Combine("Faolan.Tests", "Assets", "fixtures.bin");
				FixtureArchiveWriter.Write(ctx.CapturesDir, target);
				return 0;
			}),
			new("fixture-source", "", Needs.Captures, async ctx =>
			{
				// The archive names a capture only by an opaque id (#465); this is the way back from a failure's
				// "<id>#<index>" to the dump, by hashing every dump's name. Comma-separated ids, or every archived
				// id when none is given.
				var ids = (ctx.Arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				return FixtureArchiveWriter.ResolveCaptureIds(ctx.CapturesDir, Path.Combine("Faolan.Tests", "Assets", "fixtures.bin"), ids);
			}),
			new("conversation-states", "", Needs.Captures, async ctx =>
			{
				ConversationStates(ctx.Captures);
				return 0;
			}),
			new("conversation-fold-stability", "#643: is the graph fold append-stable? Folds the corpus, then folds it again with each capture held out, and counts the node ids that MOVE when it is added back. The measurement that ticket asks for before its option (b) can be believed.", Needs.Captures, async ctx =>
			{
				ConversationFoldStability(ctx.Captures);
				return 0;
			}),
			new("handle-recycling", "", Needs.Captures, async ctx =>
			{
				HandleRecycling(ctx.Captures);
				return 0;
			}),
			new("shield-rose", "", Needs.Captures, async ctx =>
			{
				ShieldRose(ctx.Captures);
				return 0;
			}),
			new("dynel-cycle", "", Needs.Captures, async ctx =>
			{
				DynelCycle(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("chain-kill", "", Needs.Captures, async ctx =>
			{
				ChainKill(ctx.Captures);
				return 0;
			}),
			new("dynel-effects", "", Needs.Captures, async ctx =>
			{
				using var effectsDb = OpenDb();
				DynelEffects(ctx.Captures, effectsDb);
				return 0;
			}),
			new("dot-trains", "", Needs.Captures, async ctx =>
			{
				DotTrains(ctx.Captures, ctx.Arg, _options.Out);
				return 0;
			}),
			new("buff-durations", "", Needs.Captures, async ctx =>
			{
				BuffDurations(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("morph-stats", "", Needs.Captures, async ctx =>
			{
				MorphStatsReport(ctx.Captures);
				return 0;
			}),
			new("stat-series", "", Needs.Captures, async ctx =>
			{
				StatSeries(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("weapon-set", "", Needs.Captures, async ctx =>
			{
				WeaponSetReport(ctx.Captures);
				return 0;
			}),
			new("buff-descriptor", "", Needs.Captures, async ctx =>
			{
				BuffDescriptor(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("buff-on-spawn", "", Needs.Captures, async ctx =>
			{
				BuffOnSpawn(ctx.Captures);
				return 0;
			}),
			new("coverage", "", Needs.Captures, async ctx =>
			{
				await Coverage(ctx.Captures, ctx.Arg, RdbDir());
				return 0;
			}),
			new("vitals-shape", "", Needs.Captures, async ctx =>
			{
				VitalsShape(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("anim", "", Needs.Captures, async ctx =>
			{
				Anim(ctx.Captures, ctx.Arg ?? "Casilda");
				return 0;
			}),
			new("player-actions", "", Needs.Captures, async ctx =>
			{
				// The ability catalog names the selectors: a selector IS an ability's Slot (#505).
				await using (var db = OpenDb())
					PlayerActions(ctx.Captures, await db.Abilities.Where(a => a.Slot != 0).ToDictionaryAsync(a => a.Slot, a => a.Name), ctx.Arg);

				return 0;
			}),
			new("bcc-census", "", Needs.Captures, async ctx =>
			{
				BccCensus(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("bcc-relay", "", Needs.Captures, async ctx =>
			{
				BccRelay(ctx.Captures);
				return 0;
			}),
			new("codec-sweep", "#809's generalisation: every codec against every captured record of the type it declares, over all the dumps. Parse then re-Build, byte-compared.", Needs.Captures, async ctx =>
			{
				CodecSweep(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("death-timing", "#781: every death record's `field12` against the creature that died - is the animation's length the CREATURE's, or the animation's?", Needs.Captures, async ctx =>
			{
				DeathTiming(ctx.Captures);
				return 0;
			}),
			new("corpse-timing", "What happens AFTER the death record: when the loot bag lands and where, how long the body lies there, how long an unlooted bag lasts, and whether the creature is ever seen to respawn.", Needs.Captures, async ctx =>
			{
				CorpseTiming(ctx.Captures);
				return 0;
			}),
			new("quest-xp", "#820: what retail actually PAYS for a quest - every completion in the corpus with its Exp/cash delta, with the kill-completed ones held apart, since their delta is the kill's.", Needs.Captures, async ctx =>
			{
				QuestXp(ctx.Captures);
				return 0;
			}),
			new("quest-markers", "#848/#559: every DispatchSetQuestMarker retail sent, cross-tabbed against what the shipped quest data says the NPC is worth - and the client's own five-value enum for the state byte.", Needs.Captures, async ctx =>
			{
				QuestMarkers(ctx.Captures);
				return 0;
			}),
			new("respawn-interval", "#855: WHICH property of a creature picks its respawn interval - 120 s or 150 s. corpse-timing established that retail respawns at all; this one carries the attributes and cross-tabs them.", Needs.Captures, async ctx =>
			{
				RespawnInterval(ctx.Captures);
				return 0;
			}),
			new("weapon-set-drawn", "#588/#400: does a captured spawn's worn list state which weapon SET is drawn?", Needs.Captures, async ctx =>
			{
				WeaponSetDrawn(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("travel-trips", "Which local-travel trips the captures yield - the listing the wiring module's count cannot give.", Needs.Captures, async ctx =>
			{
				TravelTrips(ctx.Captures);
				return 0;
			}),
			new("feat-procs", "#697: the last unread feat-modifier table - what Amount and Trigger mean.", Needs.Captures, async ctx =>
			{
				await FeatProcs(RdbDirOrNull());
				return 0;
			}),
			new("teleport-match", "#880: name every particle effect by the textures its payload references. #883: does the client's own teleport table line up with our door rows?", Needs.Captures, async ctx =>
			{
				await TeleportMatch(RdbDirOrNull());
				return 0;
			}),
			new("particle-effects", "", Needs.Captures, async ctx =>
			{
				ParticleEffects(RdbDirOrNull(), ctx.Arg);
				return 0;
			}),
			new("feat-keys", "#877: does a feat's TEA key ride the wire? The #251 instrument, finally given a trigger.", Needs.Captures, async ctx =>
			{
				await FeatKeys(ctx.CapturesDir, RdbDirOrNull(), ctx.Arg);
				return 0;
			}),
			new("frame-health", "How many captured frames our own strict Rpc.Parse refuses - the trust check under method-bodies.", Needs.Captures, async ctx =>
			{
				FrameHealth(ctx.Captures);
				return 0;
			}),
			new("entity-classes", "#868: which object-class word each dynel-family IIR carries - the EntityClass registry's holes.", Needs.Captures, async ctx =>
			{
				EntityClasses(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("iir-bodies", "The body dump behind every \"decode this message\" ticket - the companion to method-census.", Needs.Captures, async ctx =>
			{
				IirBodies(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("method-bodies", "", Needs.Captures, async ctx =>
			{
				MethodBodies(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("sprint", "#867: the sprint/regen family - what retail answers a sprint key with, and who it is addressed to.", Needs.Captures, async ctx =>
			{
				Sprint(ctx.Captures);
				return 0;
			}),
			new("follow-pairs", "", Needs.Captures, async ctx =>
			{
				FollowPairs(ctx.Captures);
				return 0;
			}),
			new("bcc-attack-tail", "", Needs.Captures, async ctx =>
			{
				BccAttackTail(ctx.Captures);
				return 0;
			}),
			new("ambient-animation", "", Needs.Captures, async ctx =>
			{
				AmbientAnimation(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("quest-reward-lists", "", Needs.Captures, async ctx =>
			{
				return await QuestRewardLists(ctx.Captures);
			}),
			new("quest-rewards", "", Needs.Captures, async ctx =>
			{
				return await QuestRewards(ctx.Captures, RdbDir());
			}),
			new("daily-kills", "", Needs.Captures, async ctx =>
			{
				DailyKills(ctx.Captures);
				return 0;
			}),
			new("npc-pose-check", "", Needs.Captures, async ctx =>
			{
				return await NpcPoseCheck(ctx.Captures);
			}),
			new("item-level-check", "", Needs.Captures, async ctx =>
			{
				return await ItemLevelCheck(ctx.Captures);
			}),
			new("name-filter-check", "", Needs.Captures, async ctx =>
			{
				return await NameFilterCheck(RdbDir());
			}),
			new("buff-duration-check", "", Needs.Captures, async ctx =>
			{
				return await BuffDurationCheck(ctx.Captures);
			}),
			new("spell-level-ladder", "", Needs.Captures, async ctx =>
			{
				return await SpellLevelLadder(ctx.Arg);
			}),
			new("spell-grant-check", "", Needs.Captures, async ctx =>
			{
				return await SpellGrantCheck(ctx.Arg);
			}),
			new("fixture-stats", "", Needs.Captures, async ctx =>
			{
				return FixtureStats(ctx.Arg ?? System.IO.Path.Combine("Faolan.Tests", "Assets", "fixtures.bin"));
			}),
			new("fixture-names", "", Needs.Captures, async ctx =>
			{
				return FixtureNames(System.IO.Path.Combine("Faolan.Tests", "Assets", "fixtures.bin"), ctx.Arg);
			}),
			new("rank-tables", "", Needs.Captures, async ctx =>
			{
				return await RankTables(ctx.Arg);
			}),
			new("feat-autotrain-check", "", Needs.Captures, async ctx =>
			{
				return await FeatAutotrainCheck();
			}),
			new("inventory-token-check", "", Needs.Captures, async ctx =>
			{
				return await InventoryTokenCheck();
			}),
			new("world-object-items", "", Needs.Captures, async ctx =>
			{
				return await WorldObjectItems(RdbDir());
			}),
			new("quest-container", "", Needs.Captures, async ctx =>
			{
				return QuestContainer(ctx.Captures);
			}),
			new("npc-aggression", "", Needs.Captures, async ctx =>
			{
				return await NpcAggression();
			}),
			new("interest-radius", "", Needs.Captures, async ctx =>
			{
				return InterestRadius(ctx.Captures);
			}),
			new("duplicate-records", "", Needs.Captures, async ctx =>
			{
				return DuplicateRecords(ctx.Captures);
			}),
			new("magicbuff-descriptor", "", Needs.Captures, async ctx =>
			{
				return await MagicBuffDescriptor(ctx.Captures, RdbDir());
			}),
			new("pet-cost", "", Needs.Captures, async ctx =>
			{
				return await PetCost(ctx.Captures);
			}),
			new("broadcast-check", "", Needs.Captures, async ctx =>
			{
				return BroadcastCheck(ctx.Captures);
			}),
			new("disposition-rule", "", Needs.Captures, async ctx =>
			{
				return await DispositionRule(ctx.Captures);
			}),
			new("spawn-animation", "", Needs.Captures, async ctx =>
			{
				SpawnAnimation(ctx.Captures, _options.Configuration[Keys.ContentRdbDir]);
				return 0;
			}),
			new("spawn-duplicates", "", Needs.Captures, async ctx =>
			{
				await SpawnDuplicates(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("combo-swings", "", Needs.Captures, async ctx =>
			{
				await ComboSwings(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("equip", "", Needs.Captures, async ctx =>
			{
				Equip(ctx.Captures, ctx.Arg ?? "Casilda");
				return 0;
			}),
			new("npc-damage", "", Needs.Captures, async ctx =>
			{
				NpcDamageCensus(ctx.Captures);
				return 0;
			}),
			new("npc-generic-stats", "", Needs.Captures, async ctx =>
			{
				NpcGenericStats(ctx.Captures);
				return 0;
			}),
			new("npc-debuff", "", Needs.Captures, async ctx =>
			{
				NpcDebuff(ctx.Captures);
				return 0;
			}),
			new("melee-crowd", "", Needs.Captures, async ctx =>
			{
				MeleeCrowd(ctx.Captures);
				return 0;
			}),
			new("ranged-standoff", "", Needs.Captures, async ctx =>
			{
				RangedStandoff(ctx.Captures);
				return 0;
			}),
			new("spawn-diff", "", Needs.Captures, async ctx =>
			{
				SpawnDiff(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("field-stability", "", Needs.Captures, async ctx =>
			{
				FieldStability(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("field-predictors", "", Needs.Captures, async ctx =>
			{
				FieldPredictors(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("scaffold-heap", "", Needs.Captures, async ctx =>
			{
				ScaffoldHeap(ctx.Captures, ctx.Arg, RdbDirOrNull());
				return 0;
			}),
			new("snapshot", "", Needs.Captures, async ctx =>
			{
				SnapshotScan(ctx.Captures);
				return 0;
			}),
			new("mapchange", "", Needs.Captures, async ctx =>
			{
				MapChangeScan(ctx.Captures);
				return 0;
			}),
			new("opcodes", "", Needs.Captures, async ctx =>
			{
				OpcodeScan(ctx.Captures);
				return 0;
			}),
			new("opcodes-csv", "", Needs.Captures, async ctx =>
			{
				OpcodeCsv(ctx.Captures);
				return 0;
			}),
			new("subopcodes-csv", "", Needs.Captures, async ctx =>
			{
				SubOpcodeCsv(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("worldobj", "", Needs.Captures, async ctx =>
			{
				await WorldObj(ctx.Arg ?? "hangingwoman");
				return 0;
			}),
			new("spell-fields", "", Needs.Captures, async ctx =>
			{
				SpellFields(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("rdb-assets", "", Needs.Captures, async ctx =>
			{
				RdbAssets(RdbDir(), ctx.Arg, _options.Out, _options.DumpMesh);
				return 0;
			}),
			new("rdb-skeleton", "", Needs.Captures, async ctx =>
			{
				RdbSkeleton(RdbDir(), ctx.Arg ?? "14853");
				return 0;
			}),
			new("rdb-rig", "", Needs.Captures, async ctx =>
			{
				RdbRig(RdbDir(), ctx.Arg ?? "", _options.Out, _options.Skeleton);
				return 0;
			}),
			new("rdb-search", "", Needs.Captures, async ctx =>
			{
				RdbSearch(RdbDir(), ctx.Arg ?? "");
				return 0;
			}),
			new("rdb-id", "", Needs.Captures, async ctx =>
			{
				RdbId(RdbDir(), ctx.Arg ?? "");
				return 0;
			}),
			new("rdb-dump", "", Needs.Captures, async ctx =>
			{
				RdbDump(RdbDir(), ctx.Arg ?? "", _options.Out);
				return 0;
			}),
			new("item-template", "", Needs.Captures, async ctx =>
			{
				ItemTemplate(RdbDir(), ctx.Arg ?? "", _options.Out);
				return 0;
			}),
			new("item-stat-scaling", "", Needs.Captures, async ctx =>
			{
				ItemStatScaling(RdbDir(), _options.Out);
				return 0;
			}),
			new("item-binding", "", Needs.Captures, async ctx =>
			{
				ItemBinding(ctx.Captures, RdbDir());
				return 0;
			}),
			new("spawn-item-level", "", Needs.Captures, async ctx =>
			{
				SpawnItemLevel(ctx.Captures, _options.Out);
				return 0;
			}),
			new("gem-slots", "", Needs.Captures, async ctx =>
			{
				GemSlots(RdbDir(), ctx.Captures);
				return 0;
			}),
			new("acg-pair", "", Needs.Captures, async ctx =>
			{
				AcgPair(ctx.Captures, RdbDir());
				return 0;
			}),
			new("chatter-groups", "", Needs.Captures, async ctx =>
			{
				ChatterGroups(RdbDir());
				return 0;
			}),
			new("rdb-names", "", Needs.Captures, async ctx =>
			{
				RdbNames(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("rdb-text-scan", "", Needs.Captures, async ctx =>
			{
				RdbTextScan(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("rdb-word-scan", "", Needs.Captures, async ctx =>
			{
				RdbWordScan(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("region-stat-census", "", Needs.Captures, async ctx =>
			{
				RegionStatCensus(RdbDir());
				return 0;
			}),
			new("class-attribute-runs", "", Needs.Captures, async ctx =>
			{
				ClassAttributeRuns(RdbDir(), _options.Out);
				return 0;
			}),
			new("feat-effect-widths", "", Needs.Captures, async ctx =>
			{
				FeatEffectWidths(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("item-effect-kinds", "", Needs.Captures, async ctx =>
			{
				ItemEffectKinds(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("feat-effect-kinds", "", Needs.Captures, async ctx =>
			{
				FeatEffectKinds(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("feat-record", "", Needs.Captures, async ctx =>
			{
				FeatRecord(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("feat-spell-modifiers", "", Needs.Captures, async ctx =>
			{
				FeatSpellModifiers(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("feat-table", "", Needs.Captures, async ctx =>
			{
				FeatTable(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("body-atlas-coverage", "", Needs.Captures, async ctx =>
			{
				BodyAtlasCoverage(RdbDir(), ctx.Arg, ctx.Out);
				return 0;
			}),
			new("spell-recast", "", Needs.Captures, async ctx =>
			{
				SpellRecast(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("spell-refs", "", Needs.Captures, async ctx =>
			{
				SpellRefs(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("spell-line-ids", "", Needs.Captures, async ctx =>
			{
				SpellLineIds(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("spell-mana-mode", "", Needs.Captures, async ctx =>
			{
				SpellManaMode(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("spell-element-lists", "", Needs.Captures, async ctx =>
			{
				SpellElementLists(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("spellweave", "", Needs.Captures, async ctx =>
			{
				Spellweave(RdbDir());
				return 0;
			}),
			new("spell-flags", "", Needs.Captures, async ctx =>
			{
				await SpellFlags(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("rdb-field-coverage", "", Needs.Captures, async ctx =>
			{
				RdbFieldCoverage(RdbDir(), ctx.Arg, _options.Out);
				return 0;
			}),
			new("feat-catalog", "", Needs.Captures, async ctx =>
			{
				FeatCatalog(RdbDir(), ctx.Arg, _options.Out);
				return 0;
			}),
			new("rdb-class-vitals", "", Needs.Captures, async ctx =>
			{
				RdbClassVitals(RdbDir(), string.IsNullOrWhiteSpace(ctx.Arg)
					? null
					: ctx.Arg);
				return 0;
			}),
			new("rdb-tweaks", "", Needs.Captures, async ctx =>
			{
				RdbTweaks(RdbDir(), string.IsNullOrWhiteSpace(ctx.Arg)
					? null
					: ctx.Arg);
				return 0;
			}),
			new("ability-names", "", Needs.Captures, async ctx =>
			{
				AbilityNames(RdbDir());
				return 0;
			}),
			new("method-census", "", Needs.Captures, async ctx =>
			{
				MethodCensus(ctx.Captures, ctx.Arg);
				return 0;
			}),
			new("area-banner-names", "", Needs.Captures, async ctx =>
			{
				await AreaBannerNamesReport(RdbDir(), CapturedBanners(ctx.CapturesDir));
				return 0;
			}),
			new("creature-races", "", Needs.Captures, async ctx =>
			{
				await CreatureRaces();
				return 0;
			}),
			new("create-classes", "", Needs.Captures, async ctx =>
			{
				CreateClasses(ctx.Captures);
				return 0;
			}),
			new("rdb-types", "", Needs.Captures, async ctx =>
			{
				RdbTypes(RdbDir());
				return 0;
			}),
			new("spell-record-coverage", "", Needs.Captures, async ctx =>
			{
				await SpellRecordCoverage(RdbDir());
				return 0;
			}),
			new("bcc-trees", "", Needs.Captures, async ctx =>
			{
				BccTrees(RdbDir(), ctx.Arg);
				return 0;
			}),
			new("data-table", "", Needs.Captures, async ctx =>
			{
				DataTable(RdbDir(), ctx.Arg ?? "");
				return 0;
			}),
			new("terrain-export", "", Needs.Captures, async ctx =>
			{
				await TerrainExport(RdbDir(), _options.TerrainOut ?? "Terrain");
				return 0;
			}),
			new("resolve-spawn-areas", "", Needs.Captures, async ctx =>
			{
				await ResolveSpawnAreas(RdbDir());
				return 0;
			}),
			new("spell-template", "", Needs.Captures, async ctx =>
			{
				var rdbDir = RdbDir();
				var ids = (ctx.Arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
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

						// ⭐ The element's own CONDITION run (#790/#791). Printed here because an element read
						// without its criteria is only half of what the record states - two elements with the
						// same decoded slots are routinely the same effect under different conditions, and a
						// list of them with EMPTY criteria is itself a finding (Gitea #861 turned on exactly
						// that: the rank tables condition nothing).
						foreach (var criterion in el.Criteria)
						{
							var opName = Enum.IsDefined(typeof(CriteriaOperator), criterion.Operator)
								? ((CriteriaOperator)criterion.Operator).ToString()
								: criterion.Operator.ToString();
							Console.WriteLine($"          if [{criterion.Id}] {opName} {criterion.Value}");
						}
					}
				}

				return 0;
			}),
			new("spell-stat-coverage", "", Needs.Captures, async ctx =>
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
			}),
			new("ldb-name-categories", "`--report ldb-strings --report-arg \"id:<text>\"` searches the PARSED index and prints each hit's (category, id) - as opposed to the default raw printable-run scan below, whose bracketed number is a run ordinal and NOT an id. Confusing the two sent one investigation into page-base arithmetic on numbers that were never in the id space; this is the form that answers \"what id is this text\".", Needs.Captures, async ctx =>
			{
				await LdbNameCategories(RdbDir());
				return 0;
			}),
			new("feat-name-check", "", Needs.Captures, async ctx =>
			{
				return await FeatNameCheck(RdbDir(), ctx.Arg);
			}),
			new("ldb-strings", "", Needs.Captures, async ctx =>
			{
				if ((ctx.Arg ?? "").StartsWith("id:", StringComparison.OrdinalIgnoreCase))
				{
					// `id:<text>` searches by VALUE; `id:#<number>` searches by the id itself. The second is what tells
					// a "this id is gone" story from a "this id was never an LDB id" one.
					//
					// ⭐ `id:#a,b,c` resolves SEVERAL ids in ONE pass. The scan reads every LDB page - about a minute -
					// so asking for a table's worth of ids one at a time costs that minute per id. A decoded table
					// whose column IS an LDB id (the guild renown ladder's 57 rows, #469) is the normal case, not an
					// exception.
					var wanted = ctx.Arg![3..].Trim();
					var wantedIds = new HashSet<uint>();
					var byNumber = wanted.StartsWith('#');
					if (byNumber)
					{
						foreach (var part in wanted[1..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
						{
							if (!uint.TryParse(part, out var one))
							{
								Console.WriteLine($"ldb-strings: '{part}' is not a numeric id (expected `id:#<n>` or `id:#<n>,<n>,...`)");
								return 1;
							}
	
							wantedIds.Add(one);
						}
	
						byNumber = wantedIds.Count > 0;
					}
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
								? wantedIds.Contains(entry.Id)
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

				else
				{
					var rdbDir = RdbDir();
					var needle = (ctx.Arg ?? "damage").ToLowerInvariant();
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
			}),
			new("ldb-resolve", "", Needs.Captures, async ctx =>
			{
				// #291: walk the LDB's TDC2 pages (LdbStringIndex owns that grammar) and build a global id -> string
				// map, then print lookups for the pins + any --report-ctx.Arg ids, to verify offline.
				var rdbDir = RdbDir();
				using var rdb = OpenRdb(rdbDir);
				var map = new Dictionary<uint, string>();
				var byCat = new Dictionary<(uint cat, uint id), string>();
				var perCategory = new Dictionary<uint, int>();
				// Per-page id->string for name categories, so we can crib-identify the ENGLISH page(s) against the
				// names our catalog already carries (#291/#300): 50210 spell names, 50200/50220 item names.
				var spellNamePages = new List<(uint page, Dictionary<uint, string> ids)>();
				var itemNamePages = new List<(uint cat, uint page, Dictionary<uint, string> ids)>();

				// The lookups: `--report-ctx.Arg` takes a comma-separated list of raw ids AND/OR literal TOKEN NAMES.
				// A token is hashed with the client's own key function (LdbStringIndex.TokenId), which is what turns
				// a `FUN_0061f640(<cat>, "<Token>")` call found in the exe into the string it prints - statically,
				// with no live client. Resolved per PAGE rather than off `byCat`, because every category is shipped
				// once per language and the last page to write a slot wins: an unqualified lookup silently answers
				// in Korean.
				var wanted = (ctx.Arg ?? "")
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
			}),
		];
#pragma warning restore CS1998

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
