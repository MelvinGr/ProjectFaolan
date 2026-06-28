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
		public string Name => "listings";
		public string Description => "Auction-house listings (0x2005 search results).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

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
		public string Name => "maps";
		public string Description => "Maps from MapChange packets (+ name sanitize).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

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
		public string Name => "spells";
		public string Description => "Spell ids from ApplySpell packets.";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, inserted) = await CaptureSpellImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{seen} spells seen, {inserted} new.");
		}
	}

	// Ability grants (0x2052) -> Abilities. Also rehomes the 6 archive-only ability "slot" constants (the old
	// ArchiveSqlImporter's only unique data) so the slot column survives a re-import without the archive.
	public sealed class AbilitiesModule : IImportModule
	{
		public string Name => "abilities";
		public string Description => "Ability grants (0x2052), incl. the rehomed slot seed.";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		// Ability id -> 0x2052 grant leading "slot" word. Was carried in the old archive sqlite `abilities`
		// table (mirrors the AddAbilitySlot migration backfill); kept here now the archive import is gone.
		private static readonly Dictionary<uint, uint> AbilitySlots = new()
		{
			[269272] = 106,   // Rest
			[769094] = 108,   // Normal Attack Left
			[769095] = 109,   // Normal Attack Right
			[769099] = 112,   // Normal Attack Front
			[3316611] = 1250, // toggle combat
			[3675584] = 259   // switch weapon
		};

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, added, updated) = await CaptureAbilityImporter.ImportAsync(ctx.Db, ctx.RawPackets, ctx.CreateCaptures);

			var slotted = 0;
			foreach (var ability in await ctx.Db.Abilities.ToListAsync())
				if (AbilitySlots.TryGetValue(ability.Id, out var slot) && ability.Slot != slot)
				{
					ability.Slot = slot;
					slotted++;
				}
			if (slotted > 0)
				await ctx.Db.SaveChangesAsync();

			return new ImportResult($"{seen} seen, {added} new, {updated} updated; {slotted} slot constants set.");
		}
	}

	// Per-class STARTING spells (#74) from the create captures' 0x200A grants.
	public sealed class StartingSpellsModule : IImportModule
	{
		public string Name => "starting-spells";
		public string Description => "Per-class starting spells (create 0x200A grants).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (harvested, added) = await CaptureSpellImporter.ImportClassStartingSpellsAsync(ctx.Db, ctx.CreateCaptures);
			return new ImportResult($"{harvested} harvested, {added} new.");
		}
	}

	// NPC dialogue trees, condition-tagged per capture then merged. Always a clean rebuild (handle-keyed stale
	// rows must be purged), so Overwrite-only.
	public sealed class ConversationsModule : IImportModule
	{
		public string Name => "conversations";
		public string Description => "NPC dialogue trees (per-capture condition-tagged, clean rebuild).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Overwrite };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var conversations = ConversationExtractor.Merge(
				ctx.Captures.SelectMany(c => ConversationConditions.Apply(c.Name, ConversationExtractor.Extract(c.Packets))));
			await ImportOps.ImportConversations(ctx.Db, conversations);
			return new ImportResult($"{conversations.Count} conversation nodes (rebuilt).");
		}
	}

	// Observed live character state: wallet balances + hotbar slots. Needs the characters to exist.
	public sealed class CharacterStateModule : IImportModule
	{
		public string Name => "character-state";
		public string Description => "Wallet balances + hotbar slots from captures.";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters"];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (money, shortcuts) = await CaptureCharacterStateImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{money} wallets, {shortcuts} hotbar slots seeded.");
		}
	}

	// Per-character LEARNED spell sets (#74) re-granted on login (DispatchLearnSpell). Needs characters + spells.
	public sealed class LearnedSpellsModule : IImportModule
	{
		public string Name => "learned-spells";
		public string Description => "Per-character learned spell sets (0x200A grants).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters", "spells"];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (chars, rows) = await CaptureSpellImporter.ImportLearnedSpellsAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{chars} characters seeded, {rows} new rows.");
		}
	}

	// Vendor shop windows handled in ShopsModule.cs; characters in CharactersModule.cs.

	// The burst-truth NPC set (#266): spawns + types + npc-spells + spell-type links + patrol routes. Needs
	// characters first (so player names are filtered out of the NPC-spell attribution). Overwrite = the
	// --reseed-npcs wipe (NpcSpawns + morphs/equipment + patrol only; NpcSpells left as-is).
	public sealed class NpcsModule : IImportModule
	{
		public string Name => "npcs";
		public string Description => "NPC spawns/types/spells/patrol (burst-truth set, #266).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["characters"];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Upsert, WriteMode.Overwrite };

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
			await ImportOps.LinkSpellsToTypes(ctx.Db);
			await ImportOps.ImportPatrolPaths(ctx.Db, patrols);

			var prefix = removed > 0 ? $"removed {removed}; " : "";
			return new ImportResult($"{prefix}{spawns.Count} spawns, {npcSpells.Count} npc-spell pairs ({spellsAdded} new), {patrols.Count} patrol routes.");
		}
	}

	// Static world objects (decor/containers/zones).
	public sealed class WorldObjectsModule : IImportModule
	{
		public string Name => "world-objects";
		public string Description => "Static world objects (decor/containers/zones).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

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
		public string Name => "map-loot";
		public string Description => "Per-map loot pool from captured bag contents (#273).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["items"];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Overwrite };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var entries = MapLootExtractor.Merge(ctx.Captures.SelectMany(c => MapLootExtractor.Extract(c.Packets)));
			var kept = await ImportOps.ImportMapLootPool(ctx.Db, entries);
			return new ImportResult($"{kept} map-loot entries ({entries.Count} seen).");
		}
	}

	// Per-map world-entry burst (structured IIR records). Beach (4010) is exempt (committed seed).
	public sealed class WorldBurstsModule : IImportModule
	{
		public string Name => "world-bursts";
		public string Description => "Per-map world-entry burst (structured IIR records).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Upsert, WriteMode.Overwrite };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var bursts = MapWorldBurstExtractor.Merge(ctx.Captures.SelectMany(c => MapWorldBurstExtractor.Extract(c.Packets)));
			await ImportOps.ImportMapWorldBursts(ctx.Db, bursts);
			return new ImportResult($"{bursts.Count} map bursts.");
		}
	}

	// Quest journal-content definitions + the hand-decoded snapshot/chain seeds (beach 10238, Casilda #261/#255).
	public sealed class QuestsModule : IImportModule
	{
		public string Name => "quests";
		public string Description => "Quest definitions + beach/Casilda snapshot seeds.";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

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
		public string Name => "mounts";
		public string Description => "Summonable mount creatures (#27, opt-in).";
		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CaptureMountImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} mount creature(s) found, {seeded} seeded.");
		}
	}
}
