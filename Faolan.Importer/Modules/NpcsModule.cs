using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// The burst-truth NPC set: spawns + types + npc-spells + spell-type links + patrol routes. Needs
	// characters first (so player names are filtered out of the NPC-spell attribution). Overwrite = the
	// --reseed-npcs wipe (NpcSpawns + morphs/equipment + patrol only; NpcSpells left as-is).
	public sealed class NpcsModule : IImportModule
	{
		// Depends on `items` (NpcSpawnEquipment.Template is an FK to Items - the worn item's quad + hash live there,
		// harvested by ItemHashHarvester's spawn source over the same FA015807 packets). not on `characters`: the old
		// "characters" dep was spurious - it only dragged the characters module in and added stray captured player
		// characters (e.g. Ranochar) as a side effect. RunAsync itself FKs to Map/NpcTemplate/Items, never to Characters.
		// Also on `spell-effects`: the summonable pet creatures are excluded from the world PLACEMENTS (a captured
		// session's summoned pets would otherwise become permanent spawns), and the SummonPet rows are what names them.
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule), nameof(SpellEffectsModule)];

		// Overwrite only (wipe + rebuild), like conversations. NpcSpawns is a fully-derived burst-truth set with no
		// hand-edits, so upsert is wrong: it keeps stale rows whose position changed between imports - a walked NPC
		// accretes a second row at its new spot, and a guard post accretes well past its true burst count.
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
			// Creatures that exist only as a summon - not every summonable creature. A `Companion:` vanity
			// follower is modelled on an NPC that genuinely stands in the world, and excluding those by name deleted
			// the beach's chained Casilda along with the Courtesans, Drunk Pirates and Wolf Pict Warriors.
			var petNames = await SpellEffectsModule.SummonOnlyCreatureNames(ctx.Db);
			// The maps whose population is per class (the city armories). Read from the DB, not hardcoded, so flagging
			// another map is a data change - and so a run against an empty DB simply harvests everything class-agnostic.
			var classSpecificMaps = await ctx.Db.Maps.Where(m => m.ClassSpecific).Select(m => m.Id).ToHashSetAsync();
			// every trigger-SPAWNED creature - a quest ambusher and an encounter BOSS alike. Read from the DB for
			// the same reason the pet roster is: a capture that caught one caught the trigger firing, not a placement,
			// so it must not become a permanent world spawn. The type still survives (via observedTypes), which is what
			// the trigger spawns from.
			var triggerSpawnedTypes = await ctx.Db.NpcAmbushes.Select(a => a.NpcTemplateId)
				.Concat(ctx.Db.NpcEncounters.Select(e => e.BossNpcTemplateId))
				.ToHashSetAsync();
			var (spawns, patrols, observedTypes) = NpcImportOps.BuildBurstTruthSpawns(ctx.Captures, petNames, classSpecificMaps, triggerSpawnedTypes);

			// Scoped overwrite: the wipe covers only the maps this run actually saw. The spawn set is burst-truth, so
			// it cannot be row-merged - a walked NPC accretes a second row at its new spot - but a whole-table wipe
			// makes a single dump destructive, deleting every other map's spawns. Replacing per map keeps the
			// burst-truth guarantee where the dump has authority and leaves the rest of the world alone.
			//
			// On a class-SPECIFIC map the same argument applies one level down: this run has authority over the classes
			// it actually saw, and none over the others. Wiping map 5007 wholesale while importing a necromancer trace
			// would delete the herald's armory - so the wipe is scoped to the (map, class) pairs produced. An ordinary
			// map yields the single pair (map, null) and behaves exactly as before.
			var maps = spawns.Select(s => s.MapId).Concat(patrols.Select(p => p.MapId)).Distinct().ToList();
			var classSpecificSeen = maps.Where(classSpecificMaps.Contains).ToList();
			// Nullable element type so the test is a plain `Contains(n.ClassId)` EF can turn into an in. A class-agnostic
			// row (ClassId null) standing on a class-specific map the run saw is wiped as well: on such a map every
			// placement belongs to some class by definition, so a null one is pre-flag data that would otherwise be
			// served to all twelve classes on top of their own set.
			var classesSeen = spawns.Where(s => s.ClassId != null).Select(s => (ConanClass?)s.ClassId!.Value).Distinct().ToList();
			var plainMaps = maps.Where(m => !classSpecificMaps.Contains(m)).ToList();

			var removed = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite && maps.Count > 0)
			{
				removed = await ctx.Db.NpcSpawns.CountAsync(n => plainMaps.Contains(n.MapId) || (classSpecificSeen.Contains(n.MapId) && (n.ClassId == null || classesSeen.Contains(n.ClassId))));

				// Children first - they FK the spawn, and on SQLite a cascade would fire on the parent delete
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

			await NpcImportOps.ImportNpcSpawns(ctx.Db, spawns, observedTypes);
			var npcSpells = NpcSpellExtractor.Merge(ctx.Captures.SelectMany(c => NpcSpellExtractor.Extract(c.Packets)));
			var spellsAdded = await NpcImportOps.ImportNpcSpells(ctx.Db, npcSpells);
			await NpcImportOps.ImportPatrolPaths(ctx.Db, patrols);

			var prefix = removed > 0
				? $"replaced {removed} spawn(s) across {maps.Count} map(s); "
				: "";
			return new ImportResult($"{prefix}{spawns.Count} spawns, {npcSpells.Count} npc-spell pairs ({spellsAdded} new), {patrols.Count} patrol routes.");
		}
	}
}
