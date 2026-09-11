using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Extractors;
using Faolan.Importer.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Harvests the summonable pet creatures out of the captures so a player-cast pet-summon spell renders.
	/// </summary>
	/// <remarks>
	///     Like mounts (<see cref="CaptureMountImporter" />), a pet's captured FA015807 stat core + equipment + morphs
	///     are lifted onto the creature type, and the server spawns it from the type at the summoner's position
	///     (NpcSpawnBuilder.TryBuildFromType) with no world placement at all. It is resolved at summon time by name
	///     (SummonPetForPlayer -> GetNpcByName), the name the SummonPet effect rows map each summon-spell id to.
	///     Only the NpcTemplate rows for the wanted pet creatures are touched.
	/// </remarks>
	public static class CapturePetImporter
	{
		public static async Task<(int Seen, int Seeded)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures, ILogger? logger = null)
		{
			// The SpellEffect SummonPet template names, lower-cased for matching.
			var wanted = await SpellEffectsModule.PetTemplateNames(db);

			// Every FA015807 spawn across the captures whose name is one of the wanted pets; keep the first (most
			// complete) per creature name.
			var byName = new Dictionary<string, NpcSpawnEntry>();
			var modes = new PerSpawnEventModes();
			foreach (var capture in captures)
			foreach (var e in NpcSpawnExtractor.Extract(capture))
			{
				var key = (e.Name ?? string.Empty).ToLowerInvariant();
				if (key.Length == 0 || !wanted.Contains(key) || e.Data == null)
					continue;

				byName.TryAdd(key, e);

				// Tally every sighting, not just the representative kept above: the four per-spawn-event fields are
				// corrected to the mode over all of them, while byName keeps only the first.
				var seenBody = WorldObjectColumns.ExtractBody(e.Data);
				if (seenBody != null && SpawnBodyReader.TryReadCharacterMessage(seenBody, out var seen))
					modes.Observe(e.TemplateId, seen.Message);
			}

			if (byName.Count == 0)
			{
				logger?.LogInformation("CapturePetImporter: no pet creatures found in the captures (wanted: {Wanted}).", string.Join(", ", wanted));
				return (0, 0);
			}

			var seeded = 0;
			foreach (var (_, e) in byName)
			{
				// The pet creature type: keyed by its captured TemplateId, named the creature name so the summon
				// resolves by name. Upsert so re-runs keep the type id stable.
				var type =
					await db.NpcTemplates.Include(n => n.StatCore).Include(n => n.Equipment).Include(n => n.Morphs).FirstOrDefaultAsync(n => n.TemplateId == e.TemplateId && e.TemplateId != 0) ??
					await db.NpcTemplates.Include(n => n.StatCore).Include(n => n.Equipment).Include(n => n.Morphs).FirstOrDefaultAsync(n => n.Name == e.Name);
				if (type == null)
				{
					type = new NpcTemplate
					{
						TemplateId = e.TemplateId,
						Name = e.Name
					};
					db.NpcTemplates.Add(type);
				}

				// Lift the whole spawn onto the type from its capture so a summoned pet builds wholly from columns:
				// the stat core, and, since a pet has no NpcSpawn placement, its worn equipment + appearance morphs on
				// the type too (NpcEquipment/NpcMorph).
				var body = WorldObjectColumns.ExtractBody(e.Data);
				if (body != null && SpawnBodyReader.TryReadCharacterMessage(body, out var read))
				{
					// Per-spawn-event runtime state must not become the type's definition of itself.
					if (type.StatCore == null)
					{
						type.StatCore = NpcStatCoreColumns.Lift(read.Message);
						NpcStatCoreColumns.ClearPerSpawnEventState(type.StatCore);
					}
					type.StatCore.IirHeader ??= NpcStatCoreColumns.ReadIirHeaderWord(body);

					// The four per-spawn-event fields come from the mode over every sighting, not from the one
					// representative above. A summoned creature has no NpcSpawns placement, so nothing else ever
					// revisits this row.
					modes.Apply(e.TemplateId, type.StatCore);

					type.Equipment.Clear();
					foreach (var entry in read.Message.InventoryEntries)
					{
						if (!NpcSpawnEquipmentBuilder.TryLift<NpcEquipment>(entry, out var eq))
						{
							type.Equipment.Clear(); // decline all if any item isn't byte-reproducible
							break;
						}

						type.Equipment.Add(eq);
					}

					type.Morphs.Clear();
					var morphs = NpcSpawnAppearance.TryLift(body, out var dm)
						? dm
						: NpcSpawnAppearance.TryLiftLenient(body, out var lm)
							? lm
							: [];
					for (var i = 0; i < morphs.Count; i++)
					{
						type.Morphs.Add(new NpcMorph
						{
							Ordinal = i,
							MorphId = morphs[i].MorphId,
							Value = morphs[i].Value,
							Word2 = morphs[i].Word2,
							Text = morphs[i].Text ?? ""
						});
					}
				}

				seeded++;
			}

			// The four per-spawn-event fields are corrected on every type the sweep saw, not only the representatives
			// lifted above. Same-named siblings are shadowed by the byName dedup and have no placement either, so
			// nothing else in the importer ever revisits them.
			var observedCores = await db.NpcTemplates
				.Where(n => n.StatCore != null)
				.Include(n => n.StatCore)
				.ToDictionaryAsync(n => n.TemplateId, n => n.StatCore!);
			var corrected = modes.CorrectObservedTypes(observedCores);
			if (corrected > 0)
				System.Console.WriteLine($"[pets] corrected the per-spawn-event pose on {corrected} type(s) the byName dedup shadows (#689).");

			await db.SaveChangesAsync();
			logger?.LogInformation("CapturePetImporter: lifted {Seeded} pet creature type(s) ({Names}).", seeded, string.Join(", ", byName.Keys));
			return (byName.Count, seeded);
		}
	}
}
