using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// The remaining capture-sourced modules. Each wraps an existing CaptureX importer / extractor + the shared
	// ImportOps orchestration. (items/shops/characters live in their own files.) Modes are advertised; threading
	// ctx.Mode into the underlying importer internals is the tracked follow-up (see docs/tooling/importer.md
	// "Write modes") - except where the strategy lives in ImportOps (npcs/conversations overwrite).

	// AH listings (TradepostListing) from the captured search results (0x2005).
	public sealed class ListingsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Auction-house listings (0x2005 search results).";
		public string Name => "listings";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var listings = ctx.Captures.SelectMany(c => CaptureListingImporter.Extract(c.Packets));
			// AH listings are realm-scoped (#324); join the deployment's realm (ensured before any module runs).
			var realmId = await ctx.Db.Realms.OrderBy(r => r.Id).Select(r => r.Id).FirstAsync();
			var (seen, added, updated) = await CaptureListingImporter.ImportAsync(ctx.Db, listings, realmId);
			return new ImportResult($"{seen} listings, {added} new, {updated} updated.");
		}
	}

	public sealed class StockPhrasesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Cutscene stock-phrase subtitles ({stock_N}: 0x205E req -> 0x205D resp).";
		public string Name => "stock-phrases";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var phrases = ctx.Captures.SelectMany(c => CaptureStockPhraseImporter.Extract(c.Packets));
			var (seen, added, updated) = await CaptureStockPhraseImporter.ImportAsync(ctx.Db, phrases);
			return new ImportResult($"{seen} stock phrases, {added} new, {updated} updated.");
		}
	}

	// Maps (MapChange) + the #31 tooling-suffix name sanitize backfill.
	public sealed class MapsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Maps from MapChange packets (+ name sanitize).";
		public string Name => "maps";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (imported, reflagged) = await CaptureMapImporter.ImportAsync(ctx.Db, ctx.RawPackets);

			var renamed = 0;
			foreach (var map in await ctx.Db.Maps.ToListAsync())
			{
				var clean = RdbMapImporter.SanitizeMapName(map.Name);
				if (clean != map.Name)
				{
					map.Name = clean;
					renamed++;
				}
			}

			if (renamed > 0)
				await ctx.Db.SaveChangesAsync();

			return new ImportResult($"{imported} maps imported, {reflagged} PlayfieldFlags updated, {renamed} names sanitized (#31).");
		}
	}

	// Cast/applied spell ids (ApplySpell) -> Spells.
	public sealed class SpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Spell ids from ApplySpell packets.";
		public string Name => "spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, inserted) = await CaptureSpellImporter.ImportAsync(ctx.Db, ctx.RawPackets);

			// #434: a cast whose effect lives on a different record (Frozen Hatred). Needs the packets IN ORDER per
			// capture - the pairing is "the self-buff that answered this cast" - so it takes the per-capture lists.
			var (pairs, linked) = await CaptureSpellImporter.ImportCastEffectsAsync(ctx.Db, ctx.Captures.Select(c => (IReadOnlyList<byte[]>)[.. c.Packets.Select(x => x.Bytes)]).ToList());
			return new ImportResult($"{seen} spells seen, {inserted} new, {linked} cast->effect links ({pairs} candidate pairs).");
		}
	}

	// DoT effects (StatDamageIIR 0xE9F24184) -> Spell.DotDelta/DotType (#274) + Spell.DotIntervalMs (#437).
	public sealed class StatDamageModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(SpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "DoT per-tick deltas + measured tick intervals from StatDamageIIR packets.";
		public string Name => "dot-effects";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// Per-capture packet lists, not ctx.RawPackets: the interval is measured from the TIME between ticks, and
			// the tick trains it measures are keyed by session-volatile entity handles, so they must not span captures.
			var (spells, updated, intervals) = await CaptureStatDamageImporter.ImportAsync(ctx.Db,
				[.. ctx.Captures.Select(c => (IReadOnlyList<CapturedPacket>)c.Packets)]);
			return new ImportResult($"{spells} DoT spells seen, {updated} written ({intervals} tick intervals measured).");
		}
	}

	// Ability grants (0x2052) -> Abilities.
	//
	// ⚠️ This used to carry six (abilityId -> slot) constants rehomed from the retired archive sqlite, and apply them
	// AFTER the harvest - so for those six the constant beat the wire. It is gone (#638): the 0x2052 grant's own
	// leading word is the slot (CaptureAbilityImporter), and blanking all six and re-harvesting restores every one of
	// them to the identical value, so the table was pure duplication with the wrong precedence.
	public sealed class AbilitiesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Ability grants (0x2052).";
		public string Name => "abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, added, updated) = await CaptureAbilityImporter.ImportAsync(ctx.Db, ctx.RawPackets);

			return new ImportResult($"{seen} seen, {added} new, {updated} updated.");
		}
	}

	// Per-class STARTING spells (#74) from the create captures' 0x200A grants.
	public sealed class StartingSpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-class spell access map (create 0x200A grants).";
		public string Name => "class-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (harvested, added) = await CaptureSpellImporter.ImportClassSpellsAsync(ctx.Db, ctx.CreateCaptures);
			return new ImportResult($"{harvested} harvested, {added} new.");
		}
	}

	// Corrects starter-spell unlock levels from the create grants (the level-1 truth), overriding the ellatha/wiki
	// level scrape so a level-1 create yields EXACTLY that class's f239 grant. Runs after class-spells (which harvests
	// the per-class grants); registration order (after the tsv level modules) places it after the ellatha/wiki levels.
	public sealed class StarterSpellLevelsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(StartingSpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Mark create-granted spells as each captured class's starter kit + the levels later grants land at (ClassSpell).";
		public string Name => "starter-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (starters, cleared) = await CaptureSpellImporter.MarkStarterSpellsAsync(ctx.Db, ctx.CreateCaptures);

			// #376: the same table's other half - what a ding actually grants, from every capture (not just creates).
			var (access, levels) = await CaptureSpellImporter.MarkLevelGrantsAsync(ctx.Db, ctx.Captures);
			return new ImportResult($"{starters} create-granted starters marked, {cleared} stale flags cleared; {levels} capture-proven grant levels ({access} new access rows).");
		}
	}

	// NPC dialogue trees, condition-tagged per capture then merged. Always a clean rebuild (handle-keyed stale
	// rows must be purged), so Overwrite-only.
	public sealed class ConversationsModule : IImportModule
	{
		// Conversation nodes FK NpcTemplateId (NpcsModule, #244) and GrantQuestId (QuestsModule) - both must import
		// first so the references resolve (FK is enforced on every provider; no import-time deferral).
		public IReadOnlyList<string> DependsOn => [nameof(NpcsModule), nameof(QuestsModule)];

		// UPSERT merges a dump into what is stored (#419) - drop one new capture in and its screens are added, while
		// screens already held in a more complete form are left alone. OVERWRITE rebuilds from the whole corpus.
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "NPC dialogue trees (per-capture condition-tagged; upsert merges a dump, overwrite rebuilds).";
		public string Name => "conversations";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// The variant split runs over the WHOLE corpus rather than per capture: a screen is only a conflict
			// relative to the other captures of it, and an NPC's states usually come from different dumps (#526).
			var extracted = ctx.Captures
				.SelectMany(c => ConversationExtractor.Extract(c.Packets))
				.ToList();
			var variants = new List<string>();
			var split = ConversationVariants.SplitConflicts(extracted, variants);
			foreach (var line in variants)
				ctx.Out.WriteLine($"[conversations] {line}");

			var conversations = ConversationExtractor.Merge(split);
			var overwrite = ctx.ModeFor(Name) == WriteMode.Overwrite;
			var result = await ImportOps.ImportConversations(ctx.Db, conversations, overwrite, ctx.Force);
			var held = result.Protected > 0 ? $", {result.Protected} left as hand-verified" : "";

			return new ImportResult(overwrite
				? $"{conversations.Count} conversation nodes (rebuilt){held}."
				: $"{conversations.Count} screens in the dump: {result.Inserted} new, {result.Replaced} replaced by a richer capture, {result.Kept} already as complete{held}.");
		}
	}

	// Observed live character state: wallet balances + hotbar slots. Needs the characters to exist.
	public sealed class CharacterStateModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Wallet balances + hotbar slots from captures.";
		public string Name => "character-state";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (money, shortcuts) = await CaptureCharacterStateImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{money} wallets, {shortcuts} hotbar slots seeded.");
		}
	}

	// Per-character LEARNED spell sets (#74) re-granted on login (DispatchLearnSpell). Needs characters + spells.
	public sealed class LearnedSpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(SpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character learned spell sets (0x200A grants).";
		public string Name => "learned-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (chars, rows) = await CaptureSpellImporter.ImportLearnedSpellsAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{chars} characters seeded, {rows} new rows.");
		}
	}

	// Per-character ABILITY sets (0x2052) + COMBO books (ComboBookIIR) re-granted on login. Abilities and combos are
	// NOT in the snapshot known-list, so retail re-sends them on every world entry; this persists what to replay (the
	// action bar is only shortcuts, never the grant source). Needs characters (attribution) + abilities (the catalog
	// the ability grants resolve against).
	public sealed class CharacterAbilitiesModule : IImportModule
	{
		// CharacterStateModule too: the combo pass reads the imported action bars (a type-3 shortcut is a combo), which
		// is the only per-character combo evidence for a character past the create flow.
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(AbilitiesModule), nameof(CharacterStateModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character ability sets (0x2052) + combo books (ComboBookIIR + action-bar combos).";
		public string Name => "character-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (aChars, aRows) = await CaptureAbilityImporter.ImportCharacterAbilitiesAsync(ctx.Db, ctx.RawPackets);
			var (cChars, cRows) = await CaptureAbilityImporter.ImportCharacterCombosAsync(ctx.Db, ctx.RawPackets);
			// The class<->ability access map (ClassAbility), from two sources. FIRST the CREATE captures: all 12 classes
			// have a create dump and all 12 resolve, so this covers the whole roster with each class's level-1 kit.
			// THEN the per-character grants, which can only see the four classes we imported a character for but do
			// carry their real gameplay levels. Creates-first because the character pass only inserts what it can see:
			// on its own it left DarkTemplar with ZERO abilities (its create dump has 14) and seven other classes
			// holding rows derived from characters that no longer exist - unreproducible from a clean rebuild (#296).
			var (createInserted, _) = await CaptureAbilityImporter.DeriveClassAbilitiesFromCreatesAsync(ctx.Db, ctx.CreateCaptures);
			var (caInserted, _) = await CaptureAbilityImporter.DeriveClassAbilitiesAsync(ctx.Db);
			caInserted += createInserted;
			// ClassCombo from two sources, mirroring the abilities above. The catalog's capture-validated class-name
			// prefixes (e.g. Herald_ = HoX, confirmed by Hakotep's 0x2076 combo book) give each class its own lines;
			// the create dumps give the UNIVERSAL kit those prefixes can't express, because it is named by RACE
			// (Cimmerian_/Khitan_/Stygian_/Aquilonian_Dance) yet granted to every class alike (#416).
			var (ccInserted, _) = await CaptureAbilityImporter.DeriveClassCombosAsync(ctx.Db);
			var (ucInserted, _) = await CaptureAbilityImporter.DeriveUniversalCombosFromCreatesAsync(ctx.Db, ctx.CreateCaptures);
			ccInserted += ucInserted;
			return new ImportResult($"{aRows} ability rows ({aChars} chars), {cRows} combo rows ({cChars} chars); {caInserted} ClassAbility, {ccInserted} ClassCombo rows.");
		}
	}

	// Per-character quest JOURNAL (CharacterQuest) recovered from the capture's s2c QuestIIR/QuestGoal packets, so an
	// imported character carries its active/completed quests + goal progress. Needs characters (attribution) and the
	// quest definitions (the journal references them).
	public sealed class CharacterQuestsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(QuestsModule), nameof(MapsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character quest journals (QuestIIR/QuestGoal).";
		public string Name => "character-quests";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (chars, quests) = await CaptureQuestImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{chars} characters, {quests} quest rows.");
		}
	}

	// Per-character personal BANK (CharacterInventory container kind 105) from the on-demand BankIIR packets - the
	// bank is not in the login snapshot, so it needs its own pass. Needs characters (attribution) + items (the
	// templates the bank references).
	public sealed class CharacterBankModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character bank contents (BankIIR, kind 105).";
		public string Name => "character-bank";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (chars, items) = await CaptureBankImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{chars} characters, {items} bank items.");
		}
	}

	// Vendor shop windows handled in ShopsModule.cs; characters in CharactersModule.cs.

	// The burst-truth NPC set (#266): spawns + types + npc-spells + spell-type links + patrol routes. Needs
	// characters first (so player names are filtered out of the NPC-spell attribution). Overwrite = the
	// --reseed-npcs wipe (NpcSpawns + morphs/equipment + patrol only; NpcSpells left as-is).
	public sealed class NpcsModule : IImportModule
	{
		// Depends on `items` (NpcSpawnEquipment.Template is an FK to Items - the worn item's quad + hash live there,
		// harvested by ItemHashHarvester's spawn source over the same FA015807 packets). NOT on `characters`: the old
		// "characters" dep was spurious - it only dragged the characters module in and added stray captured player
		// characters (e.g. Ranochar) as a side effect. RunAsync itself FKs to Map/NpcTemplate/Items, never to Characters.
		// Also on `spell-effects`: the summonable pet creatures are excluded from the world PLACEMENTS (a captured
		// session's summoned pets would otherwise become permanent spawns), and the SummonPet rows are what names them.
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule), nameof(SpellEffectsModule)];

		// Overwrite ONLY (wipe + rebuild), like conversations. NpcSpawns is a fully-derived burst-truth set with no
		// hand-edits, so UPSERT is wrong: it keeps stale rows whose position changed between imports (a walked NPC like
		// Prospero accreted a 2nd row at his new spot; the Noble District guards accreted to 41 vs the true burst 29).
		// A default `--only npcs` (mode Upsert) falls back to Overwrite so a re-import always replaces the set cleanly.
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "NPC spawns/types/spells/patrol (burst-truth set, #266; overwrite is scoped to the maps the run saw).";
		public string Name => "npcs";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var petNames = await SpellEffectsModule.PetTemplateNames(ctx.Db);
			// The maps whose population is per class (the city armories). Read from the DB, not hardcoded, so flagging
			// another map is a data change - and so a run against an empty DB simply harvests everything class-agnostic.
			var classSpecificMaps = await ctx.Db.Maps.Where(m => m.ClassSpecific).Select(m => m.Id).ToHashSetAsync();
			var (spawns, patrols, observedTypes) = ImportOps.BuildBurstTruthSpawns(ctx.Captures, petNames, classSpecificMaps);

			// SCOPED overwrite (#419): the wipe covers only the MAPS this run actually saw. The spawn set is
			// burst-truth, so it cannot be row-merged - a walked NPC accretes a second row at its new spot, and the
			// Noble District guards went to 41 against a true burst of 29 - but the whole-table wipe is what made a
			// single dump destructive: importing one capture deleted every other map's spawns. Replacing per map keeps
			// the burst-truth guarantee where the dump has authority and leaves the rest of the world alone.
			//
			// On a CLASS-SPECIFIC map the same argument applies one level down: this run has authority over the classes
			// it actually saw, and none over the others. Wiping map 5007 wholesale while importing a necromancer trace
			// would delete the herald's armory - so the wipe is scoped to the (map, class) pairs produced. An ordinary
			// map yields the single pair (map, null) and behaves exactly as before.
			var maps = spawns.Select(s => s.MapId).Concat(patrols.Select(p => p.MapId)).Distinct().ToList();
			var classSpecificSeen = maps.Where(classSpecificMaps.Contains).ToList();
			// Nullable element type so the test is a plain `Contains(n.ClassId)` EF can turn into an IN. A class-agnostic
			// row (ClassId null) STANDING ON a class-specific map the run saw is wiped as well: on such a map every
			// placement belongs to some class by definition, so a null one is pre-flag data that would otherwise be
			// served to all twelve classes on top of their own set.
			var classesSeen = spawns.Where(s => s.ClassId != null).Select(s => (ConanClass?)s.ClassId!.Value).Distinct().ToList();
			var plainMaps = maps.Where(m => !classSpecificMaps.Contains(m)).ToList();

			var removed = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite && maps.Count > 0)
			{
				removed = await ctx.Db.NpcSpawns.CountAsync(n => plainMaps.Contains(n.MapId) || (classSpecificSeen.Contains(n.MapId) && (n.ClassId == null || classesSeen.Contains(n.ClassId))));

				// Children first - they FK the spawn (#316), and on SQLite a cascade would fire on the parent delete
				// anyway; being explicit keeps the order obvious and works the same on Postgres.
				await ctx.Db.PatrolNodes.Where(n => plainMaps.Contains(n.NpcSpawn.MapId) || (classSpecificSeen.Contains(n.NpcSpawn.MapId) && (n.NpcSpawn.ClassId == null || classesSeen.Contains(n.NpcSpawn.ClassId)))).ExecuteDeleteAsync();
				await ctx.Db.NpcSpawnMorphs.Where(m => plainMaps.Contains(m.NpcSpawn.MapId) || (classSpecificSeen.Contains(m.NpcSpawn.MapId) && (m.NpcSpawn.ClassId == null || classesSeen.Contains(m.NpcSpawn.ClassId)))).ExecuteDeleteAsync();
				await ctx.Db.NpcSpawnEquipment.Where(e => plainMaps.Contains(e.NpcSpawn.MapId) || (classSpecificSeen.Contains(e.NpcSpawn.MapId) && (e.NpcSpawn.ClassId == null || classesSeen.Contains(e.NpcSpawn.ClassId)))).ExecuteDeleteAsync();
				await ctx.Db.NpcSpawns.Where(n => plainMaps.Contains(n.MapId) || (classSpecificSeen.Contains(n.MapId) && (n.ClassId == null || classesSeen.Contains(n.ClassId)))).ExecuteDeleteAsync();

				// ExecuteDelete bypasses the change tracker, so anything this context still holds would collide on
				// the identity map when the same derived spawn ids are re-added.
				foreach (var entry in ctx.Db.ChangeTracker.Entries<NpcSpawn>().ToList())
					entry.State = EntityState.Detached;
			}

			await ImportOps.ImportNpcSpawns(ctx.Db, spawns, observedTypes);
			var npcSpells = NpcSpellExtractor.Merge(ctx.Captures.SelectMany(c => NpcSpellExtractor.Extract(c.Packets)));
			var spellsAdded = await ImportOps.ImportNpcSpells(ctx.Db, npcSpells);
			await ImportOps.ImportPatrolPaths(ctx.Db, patrols);

			var prefix = removed > 0
				? $"replaced {removed} spawn(s) across {maps.Count} map(s); "
				: "";
			return new ImportResult($"{prefix}{spawns.Count} spawns, {npcSpells.Count} npc-spell pairs ({spellsAdded} new), {patrols.Count} patrol routes.");
		}
	}

	// NPC hostility DISPOSITION (cCategory) per NPC, harvested from the captured RequestAggroStatus responses -
	// the SOLE correct hostility source. An NPC's Community is faction MEMBERSHIP, not hostility (community 1 is the
	// civilian/default faction shared by Citizens/vendors/guards), so the old `Community > 0` aggro test wrongly made
	// every townsperson hostile. Writes NpcStatCore.Disposition (0 friendly / 1 hostile / 2 neutral); the sim gates
	// aggro (only 1 aggros) and the overhead name colour on it (#256/aggro). Depends on `npcs` (the stat cores to
	// attach to must exist).
	public sealed class NpcDispositionModule : IImportModule
	{
		private const long NullCommunity = -1; // sentinel for NPCs with no NpcStats.community

		// No hard DependsOn: it attaches to whatever NpcStatCores exist, and registration order already sequences it
		// after `npcs` in a full default run. (A hard dep would force `npcs` -> `characters` -> ... into a targeted
		// `--only npc-disposition` run; it just no-ops on an empty DB, which is fine.)
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "NPC hostility disposition (cCategory from RequestAggroStatus).";
		public string Name => "npc-disposition";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// Harvest (npc name -> cCategory votes) across all captures.
			var nameVotes = new Dictionary<string, Dictionary<uint, int>>(StringComparer.OrdinalIgnoreCase);
			foreach (var cap in ctx.Captures)
			foreach (var (name, category) in NpcDispositionExtractor.Extract(cap.Packets))
			{
				if (!nameVotes.TryGetValue(name, out var byCat))
					nameVotes[name] = byCat = [];
				byCat[category] = byCat.GetValueOrDefault(category) + 1;
			}

			// Aggregate per FACTION (NpcStats.community), NOT per NPC. Hostility toward the player is a faction
			// standing, and per-NPC cCategory is noisy: some passive court NPCs (Chancellor Publius, Ambassador Nidas,
			// ...) consistently poll cCategory 1 yet never attack and read green in-client, while the faction-level
			// majority is rock-solid (community 1 = civilians: 2649 friendly votes vs 12 hostile). Only a genuinely
			// hostile MOB faction (poachers/picts/pirates/...) comes out hostile. NULL-community NPCs (unique story
			// NPCs like Casilda/Kalanthes) share one bucket -> friendly/neutral. #256/aggro.
			var cores = await ctx.Db.NpcStatCores.ToListAsync();
			var nameById = await ctx.Db.NpcTemplates.ToDictionaryAsync(n => n.TemplateId, n => n.Name);
			var commByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			foreach (var core in cores)
			{
				if (nameById.TryGetValue(core.NpcTemplateId, out var nm) && nm != null)
				{
					commByName[nm] = core.Community.HasValue
						? core.Community.Value
						: NullCommunity;
				}
			}

			var commVotes = new Dictionary<long, Dictionary<uint, int>>();
			foreach (var (name, votes) in nameVotes)
			{
				var comm = commByName.TryGetValue(name, out var c)
					? c
					: NullCommunity;
				if (!commVotes.TryGetValue(comm, out var byCat))
					commVotes[comm] = byCat = [];
				foreach (var (cat, n) in votes)
					byCat[cat] = byCat.GetValueOrDefault(cat) + n;
			}

			var commDisposition = commVotes.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key);

			var updated = 0;
			var hostile = 0;
			foreach (var core in cores)
			{
				var comm = core.Community.HasValue
					? core.Community.Value
					: NullCommunity;
				if (!commDisposition.TryGetValue(comm, out var disposition))
					continue; // this faction was never polled -> leave NULL (treated as non-hostile in the sim)

				if (core.Disposition != disposition)
				{
					core.Disposition = disposition;
					updated++;
				}

				if (disposition == 1)
					hostile++;
			}

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{commDisposition.Count} factions dispositioned; {updated} stat cores set ({hostile} hostile).");
		}
	}

	// Per-creature death animation (the death BccIIR behaviorId), harvested from captured NPC kills (#362). Enriches
	// NpcTemplate.DeathBehaviorId by name; un-harvested types keep NULL and the runtime falls back to the humanoid id.
	public sealed class NpcDeathBehaviorsModule : IImportModule
	{
		// No hard DependsOn (mirrors NpcDispositionModule): enriches whatever NpcTemplates exist; registration order
		// sequences it after `npcs`. No-ops on an empty DB.
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-creature death animation (death BccIIR behaviorId from captured NPC kills).";
		public string Name => "npc-death-behaviors";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var harvested = NpcDeathBehaviorExtractor.Merge(ctx.Captures.SelectMany(c => NpcDeathBehaviorExtractor.Extract(c.Packets)));
			var byName = harvested.ToDictionary(d => d.Name, d => d.BehaviorId, StringComparer.OrdinalIgnoreCase);

			var cleared = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite)
			{
				foreach (var t in await ctx.Db.NpcTemplates.Where(t => t.DeathBehaviorId != null).ToListAsync())
				{
					t.DeathBehaviorId = null;
					cleared++;
				}
			}

			// A name can map to several templates (e.g. two "Nemedian Archer" types) - every matching type gets the id.
			var updated = 0;
			foreach (var template in await ctx.Db.NpcTemplates.ToListAsync())
			{
				if (template.Name != null && byName.TryGetValue(template.Name, out var behaviorId) && template.DeathBehaviorId != behaviorId)
				{
					template.DeathBehaviorId = behaviorId;
					updated++;
				}
			}

			await ctx.Db.SaveChangesAsync();
			var distinct = byName.Values.Distinct().Count();
			var prefix = cleared > 0
				? $"cleared {cleared}; "
				: "";
			return new ImportResult($"{prefix}{byName.Count} creature(s) harvested ({distinct} distinct death anims), {updated} templates set.");
		}
	}

	// Per-map area name (#348): the FIFO banner id shown on entering a playfield, harvested from captured
	// GenericDisplayAreaName feedback. Enriches Map.AreaNameId by map id.
	public sealed class AreaNamesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Named sub-areas per map + the positions their banner fires at (MapArea).";
		public string Name => "area-names";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// Every banner sighting, with the position it fired at - a map has MANY areas (Old Tarantia fires six),
			// so this is one row per (map, areaNameId), not the old one-scalar-per-map vote.
			var areas = AreaNameExtractor.MergeAreas(ctx.Captures.SelectMany(c => AreaNameExtractor.Extract(c.Packets)));
			if (areas.Count == 0)
				return new ImportResult("no area banners in the captures.");

			// Replace-all: the hull is derived wholly from the current capture set, so a stale row from a previous
			// run would keep bounds no capture supports any more.
			ctx.Db.MapAreas.RemoveRange(ctx.Db.MapAreas);
			await ctx.Db.SaveChangesAsync();

			var maps = (await ctx.Db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var added = 0;
			var skipped = 0;
			foreach (var area in areas)
			{
				if (!maps.Contains(area.MapId))
				{
					skipped++; // a banner on a playfield we never imported - would FK-fault
					continue;
				}

				ctx.Db.MapAreas.Add(area);
				added++;
			}

			await ctx.Db.SaveChangesAsync();
			var multi = areas.GroupBy(a => a.MapId).Count(g => g.Count() > 1);
			return new ImportResult(
				$"{added} named sub-areas across {areas.Select(a => a.MapId).Distinct().Count()} maps ({multi} with more than one){(skipped > 0 ? $", {skipped} on unknown maps" : "")}.");
		}
	}

	// Static world objects (decor/containers/zones).
	public sealed class WorldObjectsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Static world objects (decor/containers/zones).";
		public string Name => "world-objects";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var objects = WorldObjectExtractor.Merge(ctx.Captures.SelectMany(c => WorldObjectExtractor.Extract(c.Packets)), out var stateConflicts);

			// Overwrite deletes every stored row the harvest did not yield, so the table is by construction what the
			// captures say. Upsert cannot: it re-writes the live rows and leaves the dead ones, reporting a row-count
			// delta of zero either way, which is how the divergence went unseen (#481). ⚠️ Only a cleanup when the
			// corpus you are importing is a SUPERSET of the one that built the table - read the warning on
			// ImportOps.ImportWorldObjects before pointing it at the shipped faolan.db.
			await ImportOps.ImportWorldObjects(ctx.Db, objects, ctx.ModeFor(Name) == WriteMode.Overwrite);

			// Say when a door was captured in two different states, rather than resolving it silently (#554). The
			// resolution is deterministic, but the fact that the corpus disagreed is worth reading in the log.
			var resolved = stateConflicts == 0
				? ""
				: $" ({stateConflicts} captured in disagreeing open/closed states, resolved to the world-entry state)";
			return new ImportResult($"{objects.Count} world objects.{resolved}");
		}
	}

	// Zone-line teleport destinations: enriches the DoorFullUpdateIIR world objects that are teleporters with their
	// destination (map + arrival), harvested from captured UseObject->N3Teleport sequences (WorldTeleportExtractor).
	// Only doors actually walked through in a capture get a destination; the rest keep InteractAction=None and the
	// server surfaces a message on use. Depends on world-objects (the rows it enriches must exist).
	public sealed class WorldTeleportsModule : IImportModule
	{
		private const float PositionTolerance = 1.0f; // burst pos vs stored row pos (both from the same import)

		// No hard DependsOn (mirrors NpcDispositionModule): it enriches whatever zone-line WorldObjects already exist,
		// and registration order already sequences it right after `world-objects` in a full default run. A hard dep
		// would drag the heavy world-objects re-import into a targeted `--only world-teleports` run - which fails on a
		// populated DB (its Insert path collides on the (map,pos,name) unique key). On an empty DB this just no-ops.
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Zone-line teleport destinations (captured UseObject->N3Teleport).";
		public string Name => "world-teleports";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var links = WorldTeleportExtractor.Merge(ctx.Captures.SelectMany(c => WorldTeleportExtractor.Extract(c.Packets)));

			// Overwrite: clear any previously-written destinations so a re-import reflects only the current harvest.
			var cleared = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite)
			{
				foreach (var o in await ctx.Db.WorldObjects.Where(o => o.InteractAction != InteractAction.None).ToListAsync())
				{
					o.InteractAction = InteractAction.None;
					o.DestinationMapId = null;
					o.DestinationX = o.DestinationY = o.DestinationZ = null;
					cleared++;
				}
			}

			// Enrich BOTH door classes the extractor harvests: DoorFullUpdateIIR zone-lines AND the SimpleDynel doors
			// (e.g. the Thirsty Dog Inn) - WorldTeleportExtractor.DoorSubOpcodes.
			var zoneLines = await ctx.Db.WorldObjects.Where(o => WorldTeleportExtractor.DoorSubOpcodes.Contains(o.SubOpcode)).ToListAsync();

			var matched = 0;
			var misses = 0;
			foreach (var link in links)
			{
				var row = NearestZoneLine(zoneLines, link);
				if (row == null)
				{
					misses++;
					continue;
				}

				// Same-playfield reposition vs cross-playfield handoff - the client's Teleport vs ProxyTeleport GameAction.
				row.InteractAction = link.DestMapId == row.MapId
					? InteractAction.Teleport
					: InteractAction.ProxyTeleport;
				row.DestinationMapId = link.DestMapId;
				row.DestinationX = link.DestX;
				row.DestinationY = link.DestY;
				row.DestinationZ = link.DestZ;
				matched++;
			}

			await ctx.Db.SaveChangesAsync();

			var prefix = cleared > 0
				? $"cleared {cleared}; "
				: "";
			return new ImportResult($"{prefix}{links.Count} teleport links harvested, {matched} matched to world objects, {misses} unmatched.");
		}

		// The stored zone-line row for a harvested link: same map + name, nearest to the captured source position
		// (a name can repeat across maps; within a map the position disambiguates). Null when none is within tolerance.
		private static WorldObject? NearestZoneLine(IReadOnlyList<WorldObject> zoneLines, TeleportLink link)
		{
			WorldObject? best = null;
			var bestSq = PositionTolerance * PositionTolerance;
			foreach (var o in zoneLines)
			{
				if (o.MapId != link.SourceMapId || !string.Equals(o.Name, link.SourceName, StringComparison.Ordinal))
					continue;

				var dx = o.PositionX - link.SrcX;
				var dy = o.PositionY - link.SrcY;
				var dz = o.PositionZ - link.SrcZ;
				var sq = dx * dx + dy * dy + dz * dz;
				if (sq <= bestSq)
				{
					bestSq = sq;
					best = o;
				}
			}

			return best;
		}
	}

	// Per-map loot pool (#273): the items a map's captured loot bags carried, that NPC-kill drops on that map roll
	// from instead of the all-items random sample. Needs the items first so the pool can be filtered to buildable rows.
	public sealed class MapLootModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-map loot pool from captured bag contents (#273).";
		public string Name => "map-loot";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var entries = MapLootExtractor.Merge(ctx.Captures.SelectMany(c => MapLootExtractor.Extract(c.Packets)));
			var kept = await ImportOps.ImportMapLootPool(ctx.Db, entries);
			return new ImportResult($"{kept} map-loot entries ({entries.Count} seen).");
		}
	}

	// Quest journal-content definitions + the hand-decoded snapshot/chain seeds (beach 10238, Casilda #261/#255).
	public sealed class QuestsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(MapsModule)];

		// The capture refresh CLOBBERS the hand-authored trigger fields quest-wiring owns, so stopping here leaves the
		// quest data wrong (see IImportModule.RequiredAfter - this is the hazard that motivated it).
		public IReadOnlyList<string> RequiredAfter => [nameof(QuestWiringModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Quest definitions + areas + @609 objective goals (harvest; wiring is the quest-wiring module).";
		public string Name => "quests";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var packets = ctx.Captures.SelectMany(c => c.Packets).ToList();
			var defs = QuestDefinitionExtractor.Extract(packets);

			// Areas FIRST: QuestDefinition.QuestLineId is an FK, so every area a definition points at must already have a
			// row. The 4CCs come from the completed-quest journal (#390); the rest are placeholders.
			var areas = QuestLineExtractor.Extract(packets);
			var areaCount = await ImportOps.ImportQuestLines(ctx.Db, areas, defs.Select(d => d.QuestLineId).Where(id => id != 0).Distinct().ToList());

			await ImportOps.ImportQuestDefinitions(ctx.Db, defs, ctx.Force);

			// The completed journal carries a title + sub-line for every quest the character ever finished - far more
			// quests than any capture session touches. Additive: never disturbs the richer full-update definitions.
			var harvest = CompletedQuestExtractor.Extract(packets);
			var (addedCompleted, filledCompleted) = await ImportOps.ImportCompletedQuestDefinitions(ctx.Db, harvest);

			// The @609 objective goals - required before a quest can ride the login snapshot (#389).
			var goals = ObjectiveGoalExtractor.Extract(packets);
			var addedGoals = await ImportOps.ImportObjectiveGoals(ctx.Db, goals);

			// NB the objective-region verification does NOT run here - it runs at the end of quest-wiring, because the
			// rows it judges are not final until that overlay has re-applied the hand-authored fields this harvest
			// clobbers (goal 1448's TrailingLocalized; see IImportModule.RequiredAfter). Verifying here flagged the
			// quest against an intermediate state, which is how the beach starter stayed unverified while the SHIPPED
			// rows rebuild byte-exact (#389).

			return new ImportResult($"{defs.Count} quest definitions, {addedGoals} objective goals, {areaCount} quest areas " +
			                        $"({areas.Count} with wire data), {addedCompleted} completed-journal quests added ({filledCompleted} already known).");
		}
	}

	// The hand-authored quest WIRING overlay (#358): the server-trigger fields (GoalType/TargetName/QuestItemId), chain
	// successors (NextQuestId) and byte-exact @609/snapshot detail that captures don't carry - the Casilda + Shipwrecked
	// chains and the sourced location quests. A distinct module (not baked into `quests`) so it's selectable/skippable and
	// keeps the definition harvest pure. Runs AFTER quests (DependsOn) so it overlays last; trigger/chain seeds are
	// BOOTSTRAP-ONLY, so a re-run never reverts a Quest-editor edit (only wire fields refresh from the capture).
	public sealed class QuestWiringModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(QuestsModule), nameof(MapsModule), nameof(WorldObjectsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Hand-authored quest wiring (trigger fields + chains + @609 detail) onto the harvested definitions.";
		public string Name => "quest-wiring";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			await ImportOps.WireQuests(ctx.Db);

			// Only quests whose objective region provably rebuilds may ride the login snapshot. Verified HERE, as the
			// last thing that touches these rows: `quests` refreshes the goal fields from the captures and this overlay
			// then re-applies the hand-authored ones, so a check run before it judges bytes we never ship.
			var (verified, verifiable) = await ImportOps.VerifyObjectiveRegions(ctx.Db,
				ObjectiveGoalExtractor.CapturedRegions(ctx.Captures.SelectMany(c => c.Packets).ToList()));
			Console.WriteLine($"  objective regions verified byte-exact: {verified} of {verifiable} quests.");

			// AFTER the verification, which is the gate it reads.
			var seeded = await ImportOps.SeedDerivedSnapshotRecords(ctx.Db);
			Console.WriteLine($"  derived a Quest_c record for {seeded} quest(s) that had everything but one.");

			return new ImportResult($"quest wiring applied (Casilda + Shipwrecked chains + sourced location quests); {seeded} derived snapshot record(s).");
		}
	}

	// Summonable mount creatures (#27) into the dedicated never-loaded Mounts map. Isolated; NOT in the default
	// set, so opt-in via --only mounts.
	public sealed class MountsModule : IImportModule
	{
		// NpcEquipment.Template is an FK to Items (the worn item's quad + hash), so items must be harvested first.
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Summonable mount creatures (#27, opt-in).";
		public string Name => "mounts";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CaptureMountImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} mount creature(s) found, {seeded} seeded.");
		}
	}

	// Crafting recipe-book catalog (TradeskillReplyIIR 0x9062D559) - the list served when the client opens the
	// Recipes window. Opt-in (its only capture is one L80 char's full 603-recipe book).
	public sealed class RecipesModule : IImportModule
	{
		// Depends on `items`: every RecipeItem.ItemId is a hard FK to Items. The recipe importer harvests each
		// referenced item's identity (key + hash) into Items from the recipe element, but the items module must run
		// first so a real named item wins over the bare harvested placeholder (and the FK resolves for a `--only
		// recipes` run, which pulls items in via this dependency).
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Crafting recipes book (TradeskillReplyIIR TradeskillReplyCodec.Opcode, opt-in).";
		public string Name => "recipes";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var recipes = ctx.Captures.SelectMany(c => CaptureRecipeImporter.Extract(c.Packets)).ToList();
			var deduped = recipes.GroupBy(r => r.Id).Select(g => g.First()).ToList();
			var (seen, added, updated, harvested) = await CaptureRecipeImporter.ImportAsync(ctx.Db, deduped);
			return new ImportResult($"{seen} recipe(s), {added} new, {updated} updated, {harvested} item template(s) harvested.");
		}
	}

	// Consumable ON-USE spells (Items.OnUseSpell): pairs each used item's TemplateActionIIR with the self-ApplySpell it
	// emits, so a use-a-consumable capture teaches the DB what buff a potion/food/drink applies. Depends on items (the
	// rows it sets the column on). Upsert - it only writes items already present.
	public sealed class ConsumableUsesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule), nameof(SpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Consumable on-use spells (TemplateActionIIR + ApplySpell) -> Items.OnUseSpell.";
		public string Name => "consumable-uses";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (harvested, set) = await CaptureItemUseImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => (IReadOnlyList<byte[]>)c.Packets.Select(p => p.Bytes).ToList()));
			return new ImportResult($"{harvested} on-use spells harvested, {set} set on Items.");
		}
	}

	// Account CLAIMS (CoreClaimIIR 0x06EC1255) -> AccountClaim rows, attached to the import account. Opt-in
	// like the other account-state seeds. The item TEA keys the claims reference are harvested into Items by the items
	// module (ItemHashHarvester's claim-list source), so this depends on `items` for the FK + the key the server
	// re-sends on the wire. Overwrite = rebuild the account's claim set cleanly. See docs/subsystems/item-claims.md.
	public sealed class ClaimsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Account claims (CoreClaimIIR list) -> AccountClaim (opt-in).";
		public string Name => "claims";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var account = await ctx.Db.Accounts.FirstOrDefaultAsync(a => a.UserName == CaptureCharacterImporter.ImportAccountName);
			if (account == null)
				return new ImportResult($"no '{CaptureCharacterImporter.ImportAccountName}' account - run the characters module first; skipped.");

			var (parsed, written) = await CaptureClaimImporter.ImportAsync(ctx.Db, ctx.RawPackets, account.Id);
			return new ImportResult($"{parsed} claims parsed, {written} written to account '{CaptureCharacterImporter.ImportAccountName}'.");
		}
	}

	// Summonable PET creatures (#307): lifts each pet's captured FA015807 stat core onto its creature TYPE (NpcTemplate.Data)
	// so a pet-summon spell spawns it FROM THE TYPE (no world placement). Opt-in like mounts; run against a pet-summon
	// capture, e.g. `--only pets`.
	public sealed class PetsModule : IImportModule
	{
		// NpcEquipment.Template is an FK to Items (the worn item's quad + hash), so items must be harvested first.
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Summonable pet creatures - type-level stat core (#307, opt-in).";
		public string Name => "pets";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CapturePetImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} pet creature(s) found, {seeded} type(s) lifted.");
		}
	}
}
