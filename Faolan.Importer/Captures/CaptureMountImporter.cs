using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// MountCatalog (namespaced here despite its file living under Faolan.Core.Network)

namespace Faolan.Importer.Captures
{
	// Harvests the summonable MOUNT creatures (#27) out of the captures so a player-cast SummonMount renders. A mount
	// is a normal FA015807 NPC entity ("Fading Black Horse", ...) the server spawns at the player when they summon it;
	// it must NOT auto-spawn into the world like a map NPC. Done the RIGHT way like pets (#307/#244): its captured
	// stat core + worn equipment + appearance morphs are lifted onto the creature TYPE (no NpcSpawn placement), and the
	// server spawns it FROM THE TYPE at the summoner's position (NpcSpawnBuilder.TryBuildFromType). Resolved at summon
	// time by NAME (SpawnMountForPlayer -> GetNpcByName), the same name MountCatalog maps each mount id to.
	public static class CaptureMountImporter
	{
		public static async Task<(int Seen, int Seeded)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures, ILogger? logger = null)
		{
			// The creature names we actually need (the MountCatalog targets), lower-cased for matching.
			var wanted = MountCatalog.All.Select(m => m.CreatureName.ToLowerInvariant()).ToHashSet();

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

			var seeded = 0;
			foreach (var (_, e) in byName)
			{
				// The mount creature TYPE (#244): keyed by its captured TemplateId, named the creature name so the summon
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

				// Lift the whole spawn onto the TYPE (like pets): stat core + no-morph recordCount, and - since a mount
				// has no NpcSpawn placement - its worn equipment + appearance morphs on the type (NpcEquipment/NpcMorph),
				// so TryBuildFromType assembles the body wholly from columns (Data retired).
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
							type.Equipment.Clear();
							break;
						}

						type.Equipment.Add(eq);
					}

					type.Morphs.Clear();
					var morphs = NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var dm)
						? dm
						: NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(body, out var lm)
							? lm
							: [];
					for (var i = 0; i < morphs.Count; i++)
					{
						type.Morphs.Add(new NpcMorph
						{
							Ordinal = i,
							MorphId = morphs[i].MorphId,
							Value = morphs[i].Value,
							Text = morphs[i].Text ?? ""
						});
					}
				}

				seeded++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureMountImporter: lifted {Seeded} mount creature type(s) ({Names}).", seeded, string.Join(", ", byName.Keys));
			return (byName.Count, seeded);
		}
	}
}
