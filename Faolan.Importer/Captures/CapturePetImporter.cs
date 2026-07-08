using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests the summonable PET creatures (#307) out of the captures so a player-cast pet-summon spell renders.
	// Unlike mounts (CaptureMountImporter, which parks a creature as an NpcSpawn PLACEMENT under a sentinel map), a
	// pet is done the RIGHT way (#244): its captured FA015807 stat core is lifted onto the creature TYPE (Npc.Data),
	// and the server spawns it FROM THE TYPE at the summoner's position (NpcSpawnBuilder.TryBuildFromType) - no world
	// placement at all. Resolved at summon time by NAME (SummonPetForPlayer -> GetNpcByName), the name PetSummonCatalog
	// maps each summon-spell id to. Isolated: only touches the Npc rows for the wanted pet creatures.
	public static class CapturePetImporter
	{
		public static async Task<(int Seen, int Seeded)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures, ILogger? logger = null)
		{
			// The pet creature names we need (the PetSummonCatalog targets), lower-cased for matching.
			var wanted = PetSummonCatalog.All.Select(p => p.PetTemplate.ToLowerInvariant()).ToHashSet();

			// Every FA015807 spawn across the captures whose name is one of the wanted pets; keep the first (most
			// complete) per creature name.
			var byName = new Dictionary<string, NpcSpawnEntry>();
			foreach (var capture in captures)
			foreach (var e in NpcSpawnExtractor.Extract(capture))
			{
				var key = (e.Name ?? string.Empty).ToLowerInvariant();
				if (key.Length == 0 || !wanted.Contains(key) || e.Data == null)
					continue;

				byName.TryAdd(key, e);
			}

			if (byName.Count == 0)
			{
				logger?.LogInformation("CapturePetImporter: no pet creatures found in the captures (wanted: {Wanted}).", string.Join(", ", wanted));
				return (0, 0);
			}

			var seeded = 0;
			foreach (var (_, e) in byName)
			{
				// The pet creature TYPE (#244): keyed by its captured TemplateId, named the creature name so the summon
				// resolves by name. Upsert so re-runs keep the type id stable.
				var type = await db.Npcs.FirstOrDefaultAsync(n => n.TemplateId == e.TemplateId && e.TemplateId != 0) ?? await db.Npcs.FirstOrDefaultAsync(n => n.Name == e.Name);
				if (type == null)
				{
					type = new Npc
					{
						TemplateId = e.TemplateId,
						Name = e.Name
					};
					db.Npcs.Add(type);
				}

				// Lift the captured stat CORE onto the TYPE so the pet spawns from its type alone (#307).
				type.Community = e.Community;
				type.Race = e.Race;
				type.Sex = e.Sex;
				type.Data = e.Data;
				type.Level = e.Level;
				seeded++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CapturePetImporter: lifted {Seeded} pet creature type(s) ({Names}).", seeded, string.Join(", ", byName.Keys));
			return (byName.Count, seeded);
		}
	}
}
