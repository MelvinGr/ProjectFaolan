using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// The remaining capture-sourced modules. Each wraps an existing CaptureX importer / extractor + the shared
	// ImportOps orchestration. (items/shops/characters live in their own files.) Modes are advertised; threading
	// ctx.Mode into the underlying importer internals is the tracked follow-up (see docs/importer-plan.md
	// "Write modes") - except where the strategy lives in ImportOps (npcs/conversations overwrite).

	// AH listings (TradepostListing) from the captured search results (0x2005).
	public sealed class ListingsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Auction-house listings (0x2005 search results).";
		public string Name => "listings";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var listings = ctx.Captures.SelectMany(c => CaptureListingImporter.Extract(c.Packets));
			var (seen, added, updated) = await CaptureListingImporter.ImportAsync(ctx.Db, listings);
			return new ImportResult($"{seen} listings, {added} new, {updated} updated.");
		}
	}

	// Maps (MapChange) + the #31 tooling-suffix name sanitize backfill.
	public sealed class MapsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Maps from MapChange packets (+ name sanitize).";
		public string Name => "maps";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var imported = await CaptureMapImporter.ImportAsync(ctx.Db, ctx.RawPackets);

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

			return new ImportResult($"{imported} maps imported, {renamed} names sanitized (#31).");
		}
	}

	// Cast/applied spell ids (ApplySpell) -> Spells.
	public sealed class SpellsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Spell ids from ApplySpell packets.";
		public string Name => "spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, inserted) = await CaptureSpellImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{seen} spells seen, {inserted} new.");
		}
	}

	// DoT effects (StatDamageIIR 0xE9F24184) -> Spell.DotDelta/DotType (#274).
	public sealed class StatDamageModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["spells"];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "DoT per-tick deltas from StatDamageIIR packets.";
		public string Name => "dot-effects";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (spells, updated) = await CaptureStatDamageImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{spells} DoT spells seen, {updated} written.");
		}
	}

	// Ability grants (0x2052) -> Abilities. Also rehomes the 6 archive-only ability "slot" constants (the old
	// ArchiveSqlImporter's only unique data) so the slot column survives a re-import without the archive.
	public sealed class AbilitiesModule : IImportModule
	{
		// Ability id -> 0x2052 grant leading "slot" word. Was carried in the old archive sqlite `abilities`
		// table (mirrors the AddAbilitySlot migration backfill); kept here now the archive import is gone.
		private static readonly Dictionary<uint, uint> AbilitySlots = new Dictionary<uint, uint>
		{
			[269272] = 106, // Rest
			[769094] = 108, // Normal Attack Left
			[769095] = 109, // Normal Attack Right
			[769099] = 112, // Normal Attack Front
			[3316611] = 1250, // toggle combat
			[3675584] = 259 // switch weapon
		};

		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Ability grants (0x2052), incl. the rehomed slot seed.";
		public string Name => "abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, added, updated) = await CaptureAbilityImporter.ImportAsync(ctx.Db, ctx.RawPackets);

			var slotted = 0;
			foreach (var ability in await ctx.Db.Abilities.ToListAsync())
			{
				if (AbilitySlots.TryGetValue(ability.Id, out var slot) && ability.Slot != slot)
				{
					ability.Slot = slot;
					slotted++;
				}
			}

			if (slotted > 0)
				await ctx.Db.SaveChangesAsync();

			return new ImportResult($"{seen} seen, {added} new, {updated} updated; {slotted} slot constants set.");
		}
	}

	// Per-class STARTING spells (#74) from the create captures' 0x200A grants.
	public sealed class StartingSpellsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

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
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["class-spells"];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Upsert, WriteMode.Overwrite };
		public string Description => "Pin create-granted spells to level 1, push other class level-1 rows above it.";
		public string Name => "starter-spell-levels";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (pinned, bumped) = await CaptureSpellImporter.CorrectStarterLevelsAsync(ctx.Db, ctx.CreateCaptures);
			return new ImportResult($"{pinned} starter spells pinned to L1, {bumped} non-starter L1 rows bumped.");
		}
	}

	// NPC dialogue trees, condition-tagged per capture then merged. Always a clean rebuild (handle-keyed stale
	// rows must be purged), so Overwrite-only.
	public sealed class ConversationsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Overwrite };
		public string Description => "NPC dialogue trees (per-capture condition-tagged, clean rebuild).";
		public string Name => "conversations";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var conversations = ConversationExtractor.Merge(ctx.Captures.SelectMany(c => ConversationConditions.Apply(c.Name, ConversationExtractor.Extract(c.Packets))));
			await ImportOps.ImportConversations(ctx.Db, conversations);
			return new ImportResult($"{conversations.Count} conversation nodes (rebuilt).");
		}
	}

	// Observed live character state: wallet balances + hotbar slots. Needs the characters to exist.
	public sealed class CharacterStateModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters"];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

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
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters", "spells"];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

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
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters", "abilities"];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Per-character ability sets (0x2052) + combo books (ComboBookIIR).";
		public string Name => "character-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (aChars, aRows) = await CaptureAbilityImporter.ImportCharacterAbilitiesAsync(ctx.Db, ctx.RawPackets);
			var (cChars, cRows) = await CaptureAbilityImporter.ImportCharacterCombosAsync(ctx.Db, ctx.RawPackets);
			// Derive the class<->ability access (ClassAbility) from the per-character grants just imported (the data-driven
			// replacement for IsStartingAbility - the ability RDB template has no class/level requirement).
			var (caInserted, _) = await CaptureAbilityImporter.DeriveClassAbilitiesAsync(ctx.Db);
			// Combos have no per-character grant to derive from, so ClassCombo comes from the catalog's capture-validated
			// name prefixes (e.g. Herald_ = HoX, confirmed by Hakotep's 0x2076 combo book) - see DeriveClassCombosAsync.
			var (ccInserted, _) = await CaptureAbilityImporter.DeriveClassCombosAsync(ctx.Db);
			return new ImportResult($"{aRows} ability rows ({aChars} chars), {cRows} combo rows ({cChars} chars); {caInserted} ClassAbility, {ccInserted} ClassCombo rows.");
		}
	}

	// Per-character quest JOURNAL (CharacterQuest) recovered from the capture's s2c QuestIIR/QuestGoal packets, so an
	// imported character carries its active/completed quests + goal progress. Needs characters (attribution) and the
	// quest definitions (the journal references them).
	public sealed class CharacterQuestsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters", "quests"];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

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
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters", "items"];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

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
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters"];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Upsert,
				WriteMode.Overwrite
			};

		public string Description => "NPC spawns/types/spells/patrol (burst-truth set, #266).";
		public string Name => "npcs";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (spawns, patrols) = ImportOps.BuildBurstTruthSpawns(ctx.Captures);

			var removed = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite)
			{
				removed = await ctx.Db.NpcSpawns.CountAsync();
				await ctx.Db.Database.ExecuteSqlRawAsync("DELETE FROM NpcSpawnMorphs");
				await ctx.Db.Database.ExecuteSqlRawAsync("DELETE FROM NpcSpawnEquipment");
				await ctx.Db.Database.ExecuteSqlRawAsync("DELETE FROM NpcSpawns");
				await ctx.Db.Database.ExecuteSqlRawAsync("DELETE FROM PatrolWaypoints");
				await ctx.Db.Database.ExecuteSqlRawAsync("DELETE FROM PatrolPaths");
			}

			await ImportOps.ImportNpcSpawns(ctx.Db, spawns);
			var npcSpells = NpcSpellExtractor.Merge(ctx.Captures.SelectMany(c => NpcSpellExtractor.Extract(c.Packets)));
			var spellsAdded = await ImportOps.ImportNpcSpells(ctx.Db, npcSpells);
			await ImportOps.ImportPatrolPaths(ctx.Db, patrols);

			var prefix = removed > 0
				? $"removed {removed}; "
				: "";
			return new ImportResult($"{prefix}{spawns.Count} spawns, {npcSpells.Count} npc-spell pairs ({spellsAdded} new), {patrols.Count} patrol routes.");
		}
	}

	// Static world objects (decor/containers/zones).
	public sealed class WorldObjectsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Static world objects (decor/containers/zones).";
		public string Name => "world-objects";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var objects = WorldObjectExtractor.Merge(ctx.Captures.SelectMany(c => WorldObjectExtractor.Extract(c.Packets)));
			await ImportOps.ImportWorldObjects(ctx.Db, objects);
			return new ImportResult($"{objects.Count} world objects.");
		}
	}

	// Per-map loot pool (#273): the items a map's captured loot bags carried, that NPC-kill drops on that map roll
	// from instead of the all-items random sample. Needs the items first so the pool can be filtered to buildable rows.
	public sealed class MapLootModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["items"];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Overwrite };
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
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Quest definitions + beach/Casilda snapshot seeds.";
		public string Name => "quests";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var defs = QuestDefinitionExtractor.Extract(ctx.Captures.SelectMany(c => c.Packets).ToList());
			await ImportOps.ImportQuestDefinitions(ctx.Db, defs);
			return new ImportResult($"{defs.Count} quest definitions (+ seeds).");
		}
	}

	// Summonable mount creatures (#27) into the dedicated never-loaded Mounts map. Isolated; NOT in the default
	// set, so opt-in via --only mounts.
	public sealed class MountsModule : IImportModule
	{
		public bool InDefaultSet => false;
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Summonable mount creatures (#27, opt-in).";
		public string Name => "mounts";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CaptureMountImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} mount creature(s) found, {seeded} seeded.");
		}
	}

	// Summonable PET creatures (#307): lifts each pet's captured FA015807 stat core onto its creature TYPE (Npc.Data)
	// so a pet-summon spell spawns it FROM THE TYPE (no world placement). Opt-in like mounts; run against a pet-summon
	// capture, e.g. `--only pets`.
	// Crafting recipe-book catalog (TradeskillReplyIIR 0x9062D559) - the list served when the client opens the
	// Recipes window. Opt-in (its only capture is one L80 char's full 603-recipe book).
	public sealed class RecipesModule : IImportModule
	{
		public bool InDefaultSet => false;
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Crafting recipes book (TradeskillReplyIIR 0x9062D559, opt-in).";
		public string Name => "recipes";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var recipes = ctx.Captures.SelectMany(c => CaptureRecipeImporter.Extract(c.Packets)).ToList();
			var deduped = recipes.GroupBy(r => r.Id).Select(g => g.First()).ToList();
			var (seen, added, updated) = await CaptureRecipeImporter.ImportAsync(ctx.Db, deduped);
			return new ImportResult($"{seen} recipe(s), {added} new, {updated} updated.");
		}
	}

	public sealed class PetsModule : IImportModule
	{
		public bool InDefaultSet => false;
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Summonable pet creatures - type-level stat core (#307, opt-in).";
		public string Name => "pets";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CapturePetImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} pet creature(s) found, {seeded} type(s) lifted.");
		}
	}
}
