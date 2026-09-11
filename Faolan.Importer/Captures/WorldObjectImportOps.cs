using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Capture-import orchestration for world objects (dynels, doors, harvest nodes, zone labels) from their spawn
	///     records - the upsert helpers the modules in Faolan.Importer.Modules drive above the per-subsystem capture
	///     importers and extractors.
	/// </summary>
	public static class WorldObjectImportOps
	{
		/// <summary>
		///     Upserts static world objects (decor, containers, zone labels), deduped by their derived identity.
		///     Transient loot bags are skipped.
		/// </summary>
		/// <param name="overwrite">
		///     Makes the table exactly what the captures produce: any row the harvest did not yield is deleted. Upsert
		///     alone cannot do that, and it reports a row-count delta of zero either way, because it rewrites the live
		///     rows and leaves the dead ones.
		/// </param>
		/// <remarks>
		///     Overwrite is only a cleanup when the current corpus is a superset of the one that built the table, and
		///     the shipped corpus is curated rather than cumulative - a row can be absent from the harvest simply
		///     because the capture that showed it is no longer in it. On the shipped table the orphans include real
		///     props (zone labels, climbing points, tagged static chests), among them `decor_chain_hangingwoman`,
		///     which carries EffectQuestId 12407 and is looked up by name to seed the Casilda after-quest effect, and
		///     the Thirsty Dog Inn, the canonical same-playfield zone-line door. Deleting those loses content nothing
		///     can rebuild. So use overwrite on a table built from the same corpus being imported (a fresh database,
		///     or after a full re-harvest) and read the orphan listing the mode prints anywhere else. The transient
		///     loot-bag purge below is unconditional because it keys on what a row is rather than on whether a capture
		///     still exists.
		///     WorldObjects is the only capture-built table that drifts this way: a full default run under a global
		///     `--mode overwrite --dry-run` reports a negative delta only for it, its cascaded WorldObjectWords, and
		///     the TravelDestinations a dependent module hangs off the rows being deleted.
		/// </remarks>
		public static async Task ImportWorldObjects(DatabaseContext db, List<WorldObjectEntry> objects, bool overwrite = false)
		{
			// A dropped loot bag is a live-only world entity (built from scratch by LootBagTemplate, never persisted),
			// so it must not sit in WorldObjects. Such rows are the loot template (TemplateItemId 13782) without a
			// captured identity "[tag]" suffix
			// ("Bag of loot", item-named drops). Captured STATIC containers (e.g. "cimmerian_treasure_chest [QO2V ]")
			// keep their tag and are preserved.
			var stale = await db.WorldObjects.Where(o => o.TemplateItemId == 13782 && o.Name != null && !o.Name.Contains("]")).ToListAsync();
			if (stale.Count > 0)
			{
				db.WorldObjects.RemoveRange(stale);
				Console.WriteLine($"World objects: purged {stale.Count} transient loot-bag row(s) (live-only entities).");
			}

			// Two Adds sharing an identity collide at SaveChanges, and the existing-row query below does not see
			// un-saved Adds, so duplicates within one batch must be folded together here rather than added twice. The
			// key is the row's derived id, which is its identity (map + position + display reference). Every row goes
			// through this, including one whose display element did not lift - map and position identify it fine, and
			// exempting it blind-Adds it over its own stored row.
			var pending = new Dictionary<uint, WorldObject>();
			foreach (var e in objects)
			{
				if (e.DisplayName == "Bag of loot")
					continue;

				// Populate first: the display columns are derived by TryLiftColumns (from the object message body) and
				// can differ from e.DisplayName, so the identity must be computed from the actual stored values.
				var built = PopulateObject(new WorldObject
				{
					MapId = e.MapId,
					PositionX = e.X,
					PositionY = e.Y,
					PositionZ = e.Z
				}, e);

				built.Id = built.ComputeId();
				var id = built.Id;

				if (pending.TryGetValue(id, out var queued))
				{
					PopulateObject(queued, e); // a duplicate within this batch: fold it into the row already queued
					continue;
				}

				{
					var existing = await db.WorldObjects.FirstOrDefaultAsync(o => o.Id == id);
					if (existing != null)
					{
						// Drop the stored word rows before re-populating. The lift rebuilds them from the captured body
						// (the geometry codec clears row.Words and re-adds), but `existing` is loaded without its Words
						// - lazy loading is off - so that clear empties an already-empty collection and the re-added
						// rows collide with the stored ones: unique constraint failed on
						// (WorldObjectId, Kind, Ordinal), which a second capture of the same object hits. Including the
						// navigation instead would only trade this for the same-key delete+insert race, since the
						// replacements reuse that key - so delete immediately, as the conversation and patrol trees do.
						await db.WorldObjectWords.Where(w => w.WorldObjectId == id).ExecuteDeleteAsync();
						PopulateObject(existing, e);
						pending[id] = existing;
						continue;
					}
				}

				pending[id] = built;
				db.WorldObjects.Add(built);
			}

			Console.WriteLine("World objects: identity columns + geometry tail lifted from the dump at import (#187).");

			if (overwrite)
			{
				// Delete by derived id, not by a positional match: the id is the identity (map + position + display
				// reference), so "the harvest did not yield this row" is exactly "no built row computed this id".
				// WorldObjectWords cascades.
				//
				// Orphans are not necessarily residue, which is why this mode is not safe to fire blind on the shipped
				// table. Most are the same object filed under two playfield ids, or item-named drop dynels with no
				// captured "[tag]" identity that the loot-bag purge above misses because it keys on the template. But
				// the twin is not always the row to keep: an orphan can be the member of the pair carrying the
				// enrichment (an InteractAction and a destination map) while its twin carries neither, and re-running
				// the world-teleports module does not restore every enriched door.
				var live = pending.Keys.ToHashSet();
				var orphans = await db.WorldObjects.Where(o => !live.Contains(o.Id)).ToListAsync();
				if (orphans.Count > 0)
				{
					// Print every row with the columns that say whether it is residue or content. Read this listing
					// before trusting the mode: an orphan is only junk if the current corpus is a superset of the one
					// that built the table, and which member of a cross-map pair is the correct one is decided per map
					// pair, not by the harvest alone.
					foreach (var o in orphans.OrderBy(o => o.MapId).ThenBy(o => o.Name))
						Console.WriteLine($"  ORPHAN map {o.MapId} subop 0x{o.SubOpcode:X8} act={o.InteractAction} dest={o.DestinationMapId?.ToString() ?? "-"} eq={o.EffectQuestId?.ToString() ?? "-"} gq={o.GateQuestId?.ToString() ?? "-"} tmpl={o.TemplateItemId?.ToString() ?? "-"} :: {o.Name ?? o.GeometryName ?? "?"}");

					var enriched = orphans.Count(o => o.InteractAction != InteractAction.None || o.DestinationMapId != null || o.EffectQuestId != null || o.GateQuestId != null);
					if (enriched > 0)
						Console.WriteLine($"World objects: ⚠️  {enriched} of these carry ENRICHMENT another module wrote (quest gate / teleport destination). Deleting them drops data no capture can rebuild.");

					db.WorldObjects.RemoveRange(orphans);
					Console.WriteLine($"World objects: removed {orphans.Count} orphaned row(s) (overwrite).");
				}
				else
				{
					Console.WriteLine("World objects: no orphaned rows - the table already matches the harvest.");
				}
			}

			await db.SaveChangesAsync();
		}

		public static WorldObject PopulateObject(WorldObject row, WorldObjectEntry e)
		{
			row.SubOpcode = e.SubOpcode;
			row.Name = e.Name;
			row.ObjectId = e.ObjectId;
			row.RotationX = e.RotX;
			row.RotationY = e.RotY;
			row.RotationZ = e.RotZ;
			row.RotationW = e.RotW;

			// Lift the identity columns and geometry tail from the dump body; the captured Data is not stored.
			WorldObjectColumns.TryLiftColumns(row, WorldObjectColumns.RecordOf(WorldObjectColumns.ExtractBody(e.Data)!));
			return row;
		}
	}
}
