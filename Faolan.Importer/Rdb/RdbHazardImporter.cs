using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the LAVA hazard volumes from the client's bounded-area trigger table (RDB type 1000053 =
	// BoundedAreaCollection, #504) into MapHazard + its MapHazardVertex polygon. Records are per playfield and their id
	// == the playfield id; only maps in the Maps catalog are imported. Clean rebuild each run (the set is fully derived
	// from the fixed client RDB). Format, evidence and the full survey: docs/rdb/bounded-areas-1000053.md.
	//
	// ⚠️ LAVA ONLY, deliberately. The same records hold hundreds of other hazard-shaped names - TrapBA, BA_SpikeTrap,
	// SnakePitBA, PlayerDrowns_Dies, BA_PoisonFog, BA_WaterDamage-, BADeathBridge - and NOTHING in the corpus shows
	// what any of them do. Importing them means shipping hundreds of GUESSED kill volumes, and one wrong guess silently
	// kills players on a map nobody is testing, which is strictly worse than the feature being absent. Lava is in
	// because it is the family the wire actually pins (see MapHazard). Widening this is a small change once a capture
	// or a client script backs another family.
	public static class RdbHazardImporter
	{
		// Lava's decoded behaviour, from chat-annotated-4's death on Ymir's Pass: apply Incineration (3773837, 3 s) and
		// kill. Lethal rather than a damage number because retail sends NO StatDamage at all for that death.
		private const uint IncinerationSpellId = 3773837;

		// The id space above this is a VARIANT playfield numbering (0x40000000 | <playfield>) that is not a Map.Id -
		// `0x40000000 | 1090` and friends appear beside the real records. Skipped rather than masked: nothing shows the
		// variant is the same content, and masking would silently merge two records into one hazard set.
		private const uint VariantIdBase = 0x40000000;

		// Lava by name. Anchored on the word so `LavaDamage-3` / `BA_lava_dmg1` / `LavaArea2` / `HazardLava` all match.
		private static readonly Regex LavaName = new(@"lava", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// Names that mention lava but are NOT a damage volume: the ambient audio/music companion regions. These also
		// carry a non-zero scryid (a CLIENT script), which is the independent check applied below - the name test is
		// belt-and-braces for an export that forgot the scryid.
		private static readonly Regex NotAVolume = new(@"music|sfx|reverb|reveb|ambient|^camp\s*\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBaseUrl)
		{
			// The CDN base matters: 274 of the 478 bounded-area records are on-demand (not in the local .rdbdata
			// files), and Ymir's Pass is one of them. Without it CanRead rejects them and the import silently finds no
			// lava at all.
			using var rdb = new RdbReader(rdbDir, cdnBaseUrl);
			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();

			// Clean rebuild: the vertices cascade with their hazard, so clearing the hazards clears everything.
			db.MapHazards.RemoveRange(db.MapHazards);
			await db.SaveChangesAsync();

			var notes = new List<string>();
			int maps = 0, hazards = 0, vertices = 0, unreadable = 0, skippedAreas = 0;

			var variantRecords = 0;
			foreach (var e in rdb.EntriesOfType(BoundedAreaCollection.ResourceType).Where(rdb.CanRead).OrderBy(e => e.Id))
			{
				if (e.Id >= VariantIdBase)
				{
					// Counted, not listed per record: there are 200+ of them and listing each buries the skips that
					// actually say something about the lava.
					variantRecords++;
					continue;
				}

				if (!ourMaps.Contains(e.Id))
					continue; // a playfield we have no Map row for - not a skip worth reporting, we simply don't host it

				byte[] dec;
				try
				{
					dec = rdb.ReadDecompressed(e);
				}
				catch
				{
					unreadable++;
					continue;
				}

				if (dec.Length == 0 || !BoundedAreaCollection.TryParse(dec, out var collection) || collection == null)
				{
					unreadable++;
					continue;
				}

				var onThisMap = 0;
				foreach (var area in collection.Areas)
				{
					if (!LavaName.IsMatch(area.Name))
						continue;

					// Report every REJECTED lava-named area with its reason. A silent `continue` in an importer is data
					// loss you find out about months later (the importer_silent_skip_is_data_loss lesson): if a future
					// client export moves the damage volumes behind a scryid, these lines are what say so.
					if (area.ScryId != 0)
					{
						skippedAreas++;
						notes.Add($"map {e.Id} '{area.Name}': scryid {area.ScryId} = a CLIENT script (ambient/audio region), not a damage volume");
						continue;
					}

					if (NotAVolume.IsMatch(area.Name))
					{
						skippedAreas++;
						notes.Add($"map {e.Id} '{area.Name}': name is an ambient/label region, not a damage volume");
						continue;
					}

					if (!area.IsPolygon)
					{
						skippedAreas++;
						notes.Add($"map {e.Id} '{area.Name}': degenerate polygon ({area.Vertices.Count} vertices)");
						continue;
					}

					if (float.IsNaN(area.MinY) || float.IsNaN(area.MaxY))
					{
						skippedAreas++;
						notes.Add($"map {e.Id} '{area.Name}': no <heights> range, so the prism is unbounded vertically");
						continue;
					}

					var hazard = new MapHazard
					{
						Id = MapHazard.ComputeId(e.Id, area.Name),
						MapId = e.Id,
						Name = area.Name,
						EffectSpellId = IncinerationSpellId,
						Damage = 0,
						Lethal = true,
						MinY = area.MinY,
						MaxY = area.MaxY,
						MinX = area.Vertices.Min(v => v.X),
						MaxX = area.Vertices.Max(v => v.X),
						MinZ = area.Vertices.Min(v => v.Y),
						MaxZ = area.Vertices.Max(v => v.Y)
					};

					for (var i = 0; i < area.Vertices.Count; i++)
					{
						hazard.Vertices.Add(new MapHazardVertex
						{
							Sequence = i,
							X = area.Vertices[i].X,
							Z = area.Vertices[i].Y
						});
					}

					db.MapHazards.Add(hazard);
					hazards++;
					vertices += area.Vertices.Count;
					onThisMap++;
				}

				if (onThisMap > 0)
					maps++;
			}

			if (variantRecords > 0)
				notes.Add($"{variantRecords} records in the variant id space (0x40000000-based, not Map.Ids)");

			await db.SaveChangesAsync();
			return new Result(maps, hazards, vertices, unreadable, skippedAreas, notes);
		}

		// SkippedAreas counts lava-named AREAS that were rejected; Notes carries one human-readable line per rejection
		// plus the variant-id-space summary, so the two are never conflated in the module's one-line report.
		public readonly record struct Result(int Maps, int Hazards, int Vertices, int Unreadable, int SkippedAreas, IReadOnlyList<string> Notes);
	}
}
