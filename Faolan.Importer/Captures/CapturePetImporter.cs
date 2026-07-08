using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.World;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests the summonable PET creatures (#307) out of the captures so a player-cast pet-summon spell renders.
	// Unlike mounts (CaptureMountImporter, which parks a creature as an NpcSpawn PLACEMENT under a sentinel map), a
	// pet is done the RIGHT way (#244): its captured FA015807 stat core + equipment + morphs are lifted onto the creature TYPE,
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
				var type = await db.Npcs.Include(n => n.StatCore).Include(n => n.Equipment).Include(n => n.Morphs)
					.FirstOrDefaultAsync(n => n.TemplateId == e.TemplateId && e.TemplateId != 0)
					?? await db.Npcs.Include(n => n.StatCore).Include(n => n.Equipment).Include(n => n.Morphs)
					.FirstOrDefaultAsync(n => n.Name == e.Name);
				if (type == null)
				{
					type = new Npc
					{
						TemplateId = e.TemplateId,
						Name = e.Name
					};
					db.Npcs.Add(type);
				}


				// #307/#244: lift the whole spawn onto the TYPE from its capture so a summoned pet builds wholly from
				// columns (no Data blob) - the stat core + no-morph recordCount, and, since a pet has no NpcSpawn
				// placement, its worn equipment + appearance morphs on the type too (NpcEquipment/NpcMorph).
				var body = WorldObjectBuilder.ExtractBody(e.Data);
				if (body != null && PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				{
					type.StatCore ??= NpcStatCoreCodec.Lift(read.Message);
					type.StatCore.IirHeader ??= NpcStatCoreCodec.ReadIirHeaderWord(body);
					if (type.StatCore.MinimalPostIirRecordCount == null && NpcSpawnScaffold.TryReadNoMorphRecordCount(body, out var rc))
						type.StatCore.MinimalPostIirRecordCount = rc;

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
					var morphs = NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var dm) ? dm
						: NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(body, out var lm) ? lm
						: new();
					for (var i = 0; i < morphs.Count; i++)
						type.Morphs.Add(new NpcMorph { Ordinal = i, MorphId = morphs[i].MorphId, Value = morphs[i].Value, Text = morphs[i].Text ?? "" });
				}

				seeded++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CapturePetImporter: lifted {Seeded} pet creature type(s) ({Names}).", seeded, string.Join(", ", byName.Keys));
			return (byName.Count, seeded);
		}
	}
}
