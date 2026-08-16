using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Cli;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// Lift a creature's APPEARANCE from its placements onto its TYPE (#835): the morph table (hair, texscripts, facial
	// values) and the worn equipment, copied from a representative NpcSpawn of that template.
	//
	// A spawn built from the TYPE rather than from a placement row - the ambient spawn-area pools (#363), a summoned
	// pet, an encounter boss, `.spawnnpc` - reads `NpcTemplate.Morphs` / `.Equipment`. Only the pet and mount
	// importers ever wrote those, because a summon-only creature has no placement to carry them, so **8 of 1,213
	// types had any**: every ambient NPC in the world rendered with no hair and no clothes. Map 4010's `Poacher1`
	// pool puts eight of them on the beach next to three placed Poachers that look right, which is what it was
	// reported as - "a lot of the poachers look the same (naked/bald)".
	//
	// ⭐ **Appearance is TYPE identity, and that is measured rather than assumed**: of the 442 templates with more
	// than one placement, **442 have exactly one HeadMesh and one MonsterData across all of them**. So there is no
	// per-placement variation to lose by lifting one spawn's table onto the type - the creature genuinely looks the
	// same everywhere, and a crowd of identical Poachers is retail's own data. (The head itself already reaches the
	// wire: `NpcStatCore.HeadMesh` is type-level and always was. It is the hair and the clothes that were missing.)
	//
	// A separate module rather than a step of `npcs`, for the same reason `npc-placements` and `npc-disposition` are:
	// it only DERIVES from rows that already exist, so it re-runs against the committed faolan.db without
	// re-harvesting the spawn set. Registration order sequences it after `npcs`, which is required - it reads the
	// spawn morph/equipment rows that module writes.
	public sealed class NpcTypeAppearanceModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		// In the default set: a database without it renders every ambient creature in the world bald and naked.
		public bool InDefaultSet => true;

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		// Derived from the spawn rows already in the database - no captures, no RDB.
		public ImportSource Source => ImportSource.Db;

		public string Description => "Lift each creature's morph table + worn equipment from a placement onto its TYPE, so type-built spawns are not bald and naked (#835).";
		public string Name => "npc-type-appearance";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var overwrite = ctx.ModeFor(Name) == WriteMode.Overwrite;

			// The types that already carry an appearance. In Upsert these are left exactly as they are - the pet and
			// mount importers author theirs from a capture of the creature itself, which is better evidence than a
			// placement of a different creature, and re-deriving would fight them every run.
			var haveMorphs = await ctx.Db.NpcMorphs.Select(m => m.NpcTemplateId).Distinct().ToHashSetAsync();
			var haveEquipment = await ctx.Db.NpcEquipment.Select(e => e.NpcTemplateId).Distinct().ToHashSetAsync();

			// One representative placement per type: the LOWEST spawn id that carries anything. Deterministic, so a
			// re-run against the same database produces the same rows and faolan.sql does not churn.
			var donors = await ctx.Db.NpcSpawns.Where(s => s.Morphs.Count > 0 || s.Equipment.Count > 0)
				.Select(s => new
				{
					s.Id,
					s.NpcTemplateId
				})
				.ToListAsync();

			var representative = donors.GroupBy(s => s.NpcTemplateId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First().Id);
			var wanted = representative.Keys.Where(t => overwrite || (!haveMorphs.Contains(t) && !haveEquipment.Contains(t))).ToHashSet();
			if (wanted.Count == 0)
				return new ImportResult("every type with a placement already carries its appearance - nothing to lift.");

			var donorIds = wanted.Select(t => representative[t]).ToHashSet();
			var morphRows = await ctx.Db.NpcSpawnMorphs.Where(m => donorIds.Contains(m.NpcSpawnId)).ToListAsync();
			var equipmentRows = await ctx.Db.NpcSpawnEquipment.Where(e => donorIds.Contains(e.NpcSpawnId)).ToListAsync();
			var typeOfDonor = representative.Where(kv => wanted.Contains(kv.Key)).ToDictionary(kv => kv.Value, kv => kv.Key);

			if (overwrite)
			{
				await ctx.Db.NpcMorphs.Where(m => wanted.Contains(m.NpcTemplateId)).ExecuteDeleteAsync();
				await ctx.Db.NpcEquipment.Where(e => wanted.Contains(e.NpcTemplateId)).ExecuteDeleteAsync();
				foreach (var entry in ctx.Db.ChangeTracker.Entries<NpcMorph>().ToList())
					entry.State = EntityState.Detached;
				foreach (var entry in ctx.Db.ChangeTracker.Entries<NpcEquipment>().ToList())
					entry.State = EntityState.Detached;
			}

			ctx.Db.NpcMorphs.AddRange(morphRows.Select(m => new NpcMorph
			{
				NpcTemplateId = typeOfDonor[m.NpcSpawnId],
				Ordinal = m.Ordinal,
				MorphId = m.MorphId,
				Value = m.Value,
				Word2 = m.Word2,
				Text = m.Text ?? ""
			}));

			ctx.Db.NpcEquipment.AddRange(equipmentRows.Select(e => new NpcEquipment
			{
				NpcTemplateId = typeOfDonor[e.NpcSpawnId],
				Pos = e.Pos,
				Template = e.Template,
				Level = e.Level
			}));

			await ctx.Db.SaveChangesAsync();
			return new ImportResult($"{wanted.Count} type(s) dressed from a placement: {morphRows.Count} morph row(s) + {equipmentRows.Count} equipment row(s).");
		}
	}
}
