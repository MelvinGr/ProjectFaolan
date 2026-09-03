using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Shared capture-import orchestration the modules (Faolan.Importer.Modules) drive - the upsert/wipe-rebuild
	// helpers that sit above the per-subsystem CaptureX importers/extractors.
	// World objects (dynels, doors, harvest nodes, zone labels) from their spawn records. One of the five domain halves of what was one 2,064-line ImportOps (#928 item 7).
	public static class WorldObjectImportOps
	{
		// Static world objects (decor/containers/zones), deduped by (map, position). Transient loot bags skipped.
		//
		// `overwrite` makes the table exactly what the captures produce: any row the harvest did NOT yield is deleted.
		// Upsert alone cannot do that, and a row-count delta of zero is what it reports either way - it re-writes the
		// live rows and silently leaves the dead ones - which is why the divergence went unseen (#481).
		//
		// ⚠️ IT IS NOT SAFE TO RUN ON THE SHIPPED faolan.db, and the reason is not about this code. Overwrite is only
		// a cleanup if the CURRENT corpus is a SUPERSET of the one that built the table. Ours is not: the corpus has
		// been curated down (111 captures today; the tickets and the extract manifest were written against 213/227),
		// so a row can be absent from the harvest simply because the capture that showed it is gone. Measured on the
		// shipped table, the 62 "orphans" are mostly real props - zone labels, climbing points, tagged static chests -
		// including `decor_chain_hangingwoman`, which carries EffectQuestId 12407 and is looked up BY NAME below to
		// seed the Casilda after-quest effect, and the Thirsty Dog Inn, the canonical same-playfield zone-line door.
		// Deleting those loses content nothing can rebuild. See #481 and the ORPHAN listing the mode prints.
		//
		// So: use it on a table built from the SAME corpus you are importing (a fresh DB, or after a full re-harvest),
		// and read the listing first anywhere else. The transient-loot-bag purge below stays, because it keys on what
		// a row IS rather than on whether a capture still exists.
		//
		// ⚠️ **This table is the ONLY one that drifts**, which #481 asked and is worth not re-asking. A full default
		// run under a global `--mode overwrite --dry-run` reports a negative delta for exactly three tables, and all
		// three are this one: `WorldObjects` (-87 today, up from the 62 the ticket measured), its cascaded
		// `WorldObjectWords` (-57), and the `TravelDestinations` (-3) a dependent module hangs off the rows being
		// deleted. Every other capture module comes out even. So there is no fourth generation of residue waiting
		// elsewhere, and the shape is specific to this table rather than a general property of upsert modules.
		public static async Task ImportWorldObjects(DatabaseContext db, List<WorldObjectEntry> objects, bool overwrite = false)
		{
			// Purge any transient loot-bag rows that leaked in from old runtime persistence: a dropped loot bag is a
			// live-only world entity (built from scratch by LootBagTemplate, never DB-persisted), so it must not sit in
			// WorldObjects. They are the loot template (TemplateItemId 13782) WITHOUT a captured identity "[TAG]" suffix
			// ("Bag of loot", item-named drops). Captured STATIC containers (e.g. "cimmerian_treasure_chest [QO2V ]")
			// keep their tag and are preserved.
			var stale = await db.WorldObjects.Where(o => o.TemplateItemId == 13782 && o.Name != null && !o.Name.Contains("]")).ToListAsync();
			if (stale.Count > 0)
			{
				db.WorldObjects.RemoveRange(stale);
				Console.WriteLine($"World objects: purged {stale.Count} transient loot-bag row(s) (live-only entities).");
			}

			// Two Adds sharing an IDENTITY collide at SaveChanges - and the existing-row query below does NOT see
			// un-saved Adds, so two duplicate entries in THIS batch both fell through to Add and crashed a full import
			// (#394). Track the rows queued this batch and update the pending/existing one instead of adding a second.
			// The key is simply the row's DERIVED id, which IS the identity (map + position + display reference) - it
			// used to be a hand-built tuple mirroring the old unique index. Every row goes through this, INCLUDING one
			// whose display element did not lift: it was exempted on the reasoning that it has "no identity to dedup
			// on", which stopped being true when the derived id became the key - map + position identify it fine. The
			// exemption blind-Added it over its own stored row, so a re-import of the one such capture row (the map
			// 11040 "Siege Camp") failed with UNIQUE constraint failed: WorldObjects.Id.
			var pending = new Dictionary<uint, WorldObject>();
			foreach (var e in objects)
			{
				if (e.DisplayName == "Bag of loot")
					continue;

				// Populate FIRST: the display columns are DERIVED by TryLiftColumns (from the object message body) and
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
						// Drop the stored word rows BEFORE re-populating. The lift rebuilds them from the captured body
						// (WorldObjectGeometryCodec.TryParse does row.Words.Clear() then re-adds), but `existing` is loaded
						// without its Words - lazy loading is off - so that Clear() empties an already-empty collection and
						// the re-added rows collide with the DB rows nobody loaded:
						//   UNIQUE constraint failed: WorldObjectWords.WorldObjectId, Kind, Ordinal
						// which is what a SECOND capture of the same object (an incremental import, #419) hits. Including
						// the navigation instead would only trade this for the same-key delete+insert race, since the
						// replacements reuse (WorldObjectId, Kind, Ordinal) - so delete immediately, as the conversation
						// and patrol trees do.
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
				// Delete by DERIVED id, not by a positional match: the id IS the identity (map + position + display
				// reference), so "the harvest did not yield this row" is exactly "no built row computed this id".
				// WorldObjectWords cascades.
				//
				// ⚠️ "The enrichment columns other modules own live on rows the harvest DOES yield" is NOT true, and
				// this is the reason the mode is still not safe to fire blind on the shipped table (#481). Measured
				// 2026-08-03 against the 120-capture corpus: 2400 stored vs 2313 harvested = 87 orphans, of which
				//   * 84 have an EXACT name+position twin on another map - the same object filed under two playfield
				//     ids, which is what map-tracking residue looks like;
				//   * 3 are item-named drop dynels with no captured "[TAG]" identity (Girdle of Erlik x2, one glove) -
				//     the same transient class the TemplateItemId 13782 purge above targets, missed because that
				//     purge keys on the loot-bag template rather than on "names an item and carries no tag".
				// So none of them is unique CONTENT - but the twin is not always the row to keep. `Ymir's Pass` on map
				// 1092 is an orphan carrying InteractAction=ProxyTeleport -> 1090, while its 1090 twin carries neither,
				// and map 1092 is demonstrably a real place: the table holds the RECIPROCAL zone line (1090
				// "Amphitheatre of Karutonia" -> 1092). The table has 22 enriched doors and a world-teleports re-run
				// reproduces only 18, so an overwrite + re-enrich does not restore all of them.
				var live = pending.Keys.ToHashSet();
				var orphans = await db.WorldObjects.Where(o => !live.Contains(o.Id)).ToListAsync();
				if (orphans.Count > 0)
				{
					// Print every row, with the columns that say whether it is residue or content. ⚠️ Read this list
					// before trusting the mode: an "orphan" is only junk if the CURRENT corpus is a superset of the one
					// that built the table, and ours is not. On the shipped table they are cross-map duplicates rather
					// than lost content (see above) - but which member of a pair is the correct one is decided per map
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

			// Lift the identity columns + geometry tail from the dump body (the captured Data is no longer stored).
			WorldObjectColumns.TryLiftColumns(row, WorldObjectColumns.RecordOf(WorldObjectColumns.ExtractBody(e.Data)!));
			return row;
		}
	}
}
