using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer
{
	// Harvests the summonable MOUNT creatures (#27) out of the captures so a player-cast SummonMount renders. A mount
	// is a normal FA015807 NPC entity ("Fading Black Horse", ...) that the server spawns at the player when they
	// summon it; the only thing special about it is that it must NOT auto-spawn into the world like a map NPC. So the
	// mount creatures are seeded under a dedicated never-loaded sentinel MAP (no player ever zones there, so the world
	// tick never instantiates them) and resolved at summon time by NAME (SpawnMountForPlayer -> GetNpcSpawnByName),
	// the same name the MountCatalog maps each mount id to. Isolated: touches only the Mounts map + its NpcSpawns.
	public static class CaptureMountImporter
	{
		// A sentinel map id no real playfield uses, so the world simulation never loads (and never world-spawns) the
		// mount creatures parked under it - they exist purely as summon templates resolved by name.
		public const uint MountMapId = 0x4D4F554Eu; // 'MOUN'

		public static async Task<(int Seen, int Seeded)> ImportAsync(IDatabaseContext db,
			IEnumerable<IReadOnlyList<CapturedPacket>> captures, ILogger logger = null)
		{
			// The creature names we actually need (the MountCatalog targets), lower-cased for matching.
			var wanted = MountCatalog.All
				.Select(m => m.CreatureName.ToLowerInvariant())
				.ToHashSet();

			// Every FA015807 spawn across the captures whose name is one of the wanted mounts; keep the first (most
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
				logger?.LogInformation("CaptureMountImporter: no mount creatures found in the captures (wanted: {Wanted}).", string.Join(", ", wanted));
				return (0, 0);
			}

			// Ensure the sentinel Mounts map exists (NpcSpawn.MapId is an FK into Maps).
			if (!await db.Maps.AnyAsync(m => m.Id == MountMapId))
				db.Maps.Add(new Map { Id = MountMapId, Name = "Mounts (summon templates)" });

			// Replace any prior mount rows so a re-run reseeds cleanly (isolated to the sentinel map).
			var stale = await db.NpcSpawns.Where(n => n.MapId == MountMapId).ToListAsync();
			db.NpcSpawns.RemoveRange(stale);
			await db.SaveChangesAsync();

			var seeded = 0;
			foreach (var (name, e) in byName)
			{
				// The mount NPC TYPE (#244): keyed by its captured TemplateId, named the creature name so the summon
				// resolves by name. Upsert so re-runs keep the type id stable.
				var type = await db.Npcs.FirstOrDefaultAsync(n => n.TemplateId == e.TemplateId && e.TemplateId != 0)
					?? await db.Npcs.FirstOrDefaultAsync(n => n.Name == e.Name);
				if (type == null)
				{
					type = new Npc { TemplateId = e.TemplateId, Name = e.Name, Community = e.Community };
					db.Npcs.Add(type);
					await db.SaveChangesAsync();
				}

				db.NpcSpawns.Add(new NpcSpawn
				{
					MapId = MountMapId,
					NpcId = type.Id,
					Data = e.Data,
					CapturedHandle = e.Handle,
					PositionX = e.X,
					PositionY = e.Y,
					PositionZ = e.Z,
					RotationX = e.RotX,
					RotationY = e.RotY,
					RotationZ = e.RotZ,
					RotationW = e.RotW,
					Level = e.Level,
					Health = e.Health,
					Race = e.Race,
					Sex = e.Sex,
					HeadMesh = e.HeadMesh,
					MonsterData = e.MonsterData,
					MonsterScale = e.MonsterScale,
					HasEquipment = e.HasEquipment
				});
				seeded++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureMountImporter: seeded {Seeded} mount creature(s) under map 0x{Map:X} ({Names}).",
				seeded, MountMapId, string.Join(", ", byName.Keys));
			return (byName.Count, seeded);
		}
	}
}
