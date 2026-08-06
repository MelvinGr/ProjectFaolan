using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests the summonable MOUNT creatures (#27) out of the captures so a player-cast SummonMount renders. A mount
	// is a normal FA015807 NPC entity ("Fading Black Horse", ...) the server spawns at the player when they summon it;
	// it must NOT auto-spawn into the world like a map NPC. Done the RIGHT way like pets (#307/#244): its captured
	// stat core + worn equipment + appearance morphs are lifted onto the creature TYPE (no NpcSpawn placement), and the
	// server spawns it FROM THE TYPE at the summoner's position (NpcSpawnBuilder.TryBuildFromType). Resolved at summon
	// time by NAME (SpawnMountForPlayer -> GetNpcByName), which is the name of the mount's summon SPELL (#627).
	public static class CaptureMountImporter
	{
		public static async Task<(int Seen, int Seeded)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures, ILogger? logger = null)
		{
			// The creature names we actually need - every mount in the catalog (#627: the Mounts table, keyed by the
			// summon spell whose NAME is the creature's), lower-cased for matching. Only a handful will ever match:
			// the corpus contains the mounts the captured sessions rode, not all 155.
			var wanted = (await db.Mounts.Include(m => m.Spell).Select(m => m.Spell.Name).ToListAsync()).Select(n => n.ToLowerInvariant()).ToHashSet();
			if (wanted.Count == 0)
				throw new InvalidOperationException(
					"CaptureMountImporter: the Mounts catalog is empty, so there is nothing to match the captured spawns against. It is imported from the RDB spell records - run `--only rdb-spells --RdbDir <dir>` first (#627).");

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

				// Lift the whole spawn onto the TYPE (like pets): stat core, and - since a mount
				// has no NpcSpawn placement - its worn equipment + appearance morphs on the type (NpcEquipment/NpcMorph),
				// so TryBuildFromType assembles the body wholly from columns (Data retired).
				var body = WorldObjectColumns.ExtractBody(e.Data);
				if (body != null && SpawnBodyReader.TryReadCharacterMessage(body, out var read))
				{
					// Per-spawn-event runtime state must not become the TYPE's definition of itself (#602).
					if (type.StatCore == null)
					{
						type.StatCore = NpcStatCoreColumns.Lift(read.Message);
						NpcStatCoreColumns.ClearPerSpawnEventState(type.StatCore);
					}
					type.StatCore.IirHeader ??= NpcStatCoreColumns.ReadIirHeaderWord(body);

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

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureMountImporter: lifted {Seeded} mount creature type(s) ({Names}).", seeded, string.Join(", ", byName.Keys));
			return (byName.Count, seeded);
		}
	}
}
