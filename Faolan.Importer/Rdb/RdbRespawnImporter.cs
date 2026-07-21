using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports resurrection points from the client RDB RESURRECTIONPOINTSEXPORT (type 1000090). These records are NOT in
	// the local NN.rdbdata files (le.idx file==0xFF) - they are on-demand CDN content, so like every other RDB importer
	// this hands the mirror-proxy base to RdbReader and just calls ReadDecompressed: the reader already resolves a CDN
	// entry by content hash, unwraps IOz1+zlib, and negative-caches misses, and the proxy is store-and-forward so the
	// pull is archived for preservation. The payload is XML <resurrectionpoint ...><position x y z>; the record id is
	// the playfield/map id, discoverable="false" => AlwaysKnown (the starter). See docs/rdb/rdb-extraction-plan.md.
	// UPSERTS the real per-map data for the maps we have, keyed on the RDB Guid.
	//
	// The `name` attribute is the world EDITOR's object label, not a display name, and it is not unique: 95 points are
	// called "ResurrectionAIPoint0", 39 "ResurrectionAIPoint1", ... with only the odd designer-written one ("Outlaw
	// Camp", "Entrance"). The element carries no display-name / text-id attribute at all - the full set is Guid, name,
	// discoverable, Orientation, RestorationLevel, shady, FollowsHeightmap, HeightmapOffset, IsLocked, IsVisible,
	// BoundingBoxColor, ShowBoundingBox + Export* metadata. So a player-facing name is NOT recoverable from this file
	// (#372); it lives in the LDB string index or server-side. Don't go looking for it here again.
	//
	// Imported alongside position/discoverable: RestorationLevel (a PERCENT - observed 7/10/20/25/30/50/70/100, with
	// 344 points at 10 and 176 at 100) and Orientation (a YAW in RADIANS - all 553 values in [0, 2pi), 265 non-zero -
	// stored as the yaw-only facing quaternion; the importer previously wrote no rotation at all, leaving every point
	// at the identity).
	public static class RdbRespawnImporter
	{
		private const uint ResurrectionPointsType = 1000090;

		private static readonly Regex AttrRx = new Regex("\\b([A-Za-z_]+)=\"([^\"]*)\"", RegexOptions.Compiled);

		// Group 1 = the element's whole attribute run, 2/3/4 = position. Attributes are pulled out of group 1 by name
		// (AttrRx) rather than matched in a fixed order: the attribute SET genuinely varies between records - most
		// carry the full editor block (IsLocked/IsVisible/FollowsHeightmap/BoundingBoxColor/...), others only
		// Guid/name/RestorationLevel/discoverable/Orientation - so an order-dependent pattern is fragile for no gain.
		private static readonly Regex PointRx = new Regex("<resurrectionpoint\\b([^>]*)>\\s*<position\\s+x=\"([-0-9.]+)\"\\s+y=\"([-0-9.]+)\"\\s+z=\"([-0-9.]+)\"",
			RegexOptions.Compiled | RegexOptions.Singleline);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string proxyBase)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			var mapNames = await db.Maps.ToDictionaryAsync(m => m.Id, m => m.Name ?? "");
			var ourMaps = mapNames.Keys.ToHashSet();

			// UPSERT keyed on the RDB Guid, rather than the delete-and-rebuild this used to do (#373). The Guid is the
			// client's resurrection-point id - the spawnPointId the 0x2024 release screen sends (#214) - so re-importing
			// is idempotent and binds/discoveries survive.
			//
			// ⚠️ The Guid is NOT globally unique, only unique PER MAP: 17 of them repeat across maps because the variant
			// playfields (Tortage Island 4010 vs "Tortage Island - RIVERS_AUDIO" 944, etc.) are copies of the same place
			// with the same points exported into each. 557 parsed points collapse to 538 rows. Rather than a PK change
			// to (MapId, Guid) - churny, and pointless since all real collisions are at the SAME position and RespawnMe
			// echoes only the Guid - the collision is resolved DETERMINISTICALLY in the loop below (#374): the better-
			// ranked map keeps the Guid (a real playfield beats a variant copy, tie-break lowest id), killing the
			// RDB-order coin-flip that could otherwise strip a real map's points and hand them to an unreachable variant.
			//
			// The old wipe could not run at all on a DB with a bound character: CharacterRespawnPoints cascades, but
			// Characters.BindRespawnPointId also FKs RespawnPoints with NO cascade, so any bind pinned the row and the
			// whole module died with "FOREIGN KEY constraint failed" before it fetched anything. It only ever worked on
			// a DB where nobody had bound. Upserting keeps binds + discoveries intact across a re-import, which is what
			// we want anyway: they are savegame state, not catalog data.
			var existing = (await db.RespawnPoints.ToListAsync()).ToDictionary(p => p.Id);
			var seen = new HashSet<uint>();
			var processedMaps = new HashSet<uint>();

			int maps = 0, points = 0, fetchFailed = 0, empty = 0;
			foreach (var e in rdb.EntriesOfType(ResurrectionPointsType).Where(e => ourMaps.Contains(e.Id)))
			{
				if (!rdb.CanRead(e))
				{
					fetchFailed++; // no local file and no reachable mirror (or an already-negative-cached miss)
					continue;
				}

				byte[] dec;
				try
				{
					dec = rdb.ReadDecompressed(e);
				}
				catch
				{
					fetchFailed++;
					continue;
				}

				var xml = Encoding.Latin1.GetString(dec);
				var found = 0;
				foreach (Match m in PointRx.Matches(xml))
				{
					if (!uint.TryParse(Attr(m, "Guid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var guid))
						continue; // no Guid = not a real point (the <resurrectionpoints> container element)

					if (!existing.TryGetValue(guid, out var point))
					{
						point = new RespawnPoint
						{
							Id = guid
						}; // the client RDB Guid - the spawnPointId the release screen needs (#214)
						existing[guid] = point;
						db.RespawnPoints.Add(point);
					}

					// #374: the Guid is unique only PER MAP - 17 repeat across maps because the variant playfields
					// (Tortage Island 4010 vs "- RIVERS_AUDIO" 944, Conarch Village 1000 vs "- RIVERS", ...) are copies
					// of the same place, same points exported into each (all real collisions are at the SAME position).
					// Keying on the bare Guid means the map processed last wins by RDB enumeration ORDER - a coin-flip
					// that can silently strip a REAL playfield's points and hand them to a dev/audio variant nobody can
					// reach. Resolve it deterministically instead of a PK change (option 2 on the issue): on a collision
					// keep the better-RANKED map (a real playfield beats a variant copy; tie-break lowest map id). Since
					// the colliding points are physically identical, this only stabilises the MapId attribution.
					if (seen.Contains(guid))
					{
						if (MapRankAtLeast(point.MapId, e.Id, mapNames))
							continue; // the map that already claimed this Guid this run outranks e.Id - keep it
						// e.Id outranks the current owner (real beats variant / lower id) - fall through and re-claim it
					}
					else
						seen.Add(guid);

					point.MapId = e.Id;
					point.Name = Attr(m, "name") ?? "";
					point.AlwaysKnown = string.Equals(Attr(m, "discoverable"), "false", StringComparison.OrdinalIgnoreCase);
					point.RestorationLevel = Num(m, "RestorationLevel");
					point.PositionX = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
					point.PositionY = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
					point.PositionZ = float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);

					// `Orientation` is a YAW in RADIANS (all 553 values fall in [0, 2pi); max seen 6.278, and 265 are
					// non-zero, so it is real data rather than an unset field). Store it as the codebase's yaw-only
					// facing quaternion - the (0, qy, 0, qw) shape every captured MoveTask facing uses. Until now the
					// importer never wrote a rotation at all, so every point sat at the identity (facing +Z).
					var yaw = Num(m, "Orientation");
					point.Rotation = new Quaternion(0f, MathF.Sin(yaw / 2f), 0f, MathF.Cos(yaw / 2f));
					found++;
				}

				if (found > 0)
				{
					maps++;
					points += found;
					processedMaps.Add(e.Id);
				}
				else
					empty++;
			}

			// Drop points that vanished from the RDB, but ONLY on maps we actually re-read this run (a fetch failure
			// must not wipe a map's points). Clear any bind pointing at them first: Characters.BindRespawnPointId has
			// no cascade, so deleting a bound point is the FK failure that broke this module (#373). A character whose
			// bind is dropped re-binds on next entry via the first-login auto-bind path.
			var stale = existing.Values.Where(p => processedMaps.Contains(p.MapId) && !seen.Contains(p.Id)).ToList();
			if (stale.Count > 0)
			{
				var staleIds = stale.Select(p => p.Id).ToHashSet();
				foreach (var c in await db.Characters.Where(c => c.BindRespawnPointId != null).ToListAsync())
				{
					if (staleIds.Contains(c.BindRespawnPointId!.Value))
						c.BindRespawnPointId = null;
				}

				db.RespawnPoints.RemoveRange(stale); // CharacterRespawnPoints cascades
			}

			await db.SaveChangesAsync();
			return new Result(maps, points, empty, fetchFailed);
		}

		private static string? Attr(Match m, string name)
		{
			foreach (Match a in AttrRx.Matches(m.Groups[1].Value))
			{
				if (string.Equals(a.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
					return a.Groups[2].Value;
			}

			return null;
		}

		// #374 collision ranking: true when the map that already owns a Guid this run (`current`) is as-good-or-better
		// than the `candidate` re-claiming it, so the current owner is KEPT. Rank = (isVariant ? 1 : 0, mapId), lower
		// wins - a real playfield (0) beats a variant copy (1); among equals the lowest map id wins for stability.
		private static bool MapRankAtLeast(uint current, uint candidate, IReadOnlyDictionary<uint, string> names)
		{
			return Rank(current, names).CompareTo(Rank(candidate, names)) <= 0;
		}

		private static (int Variant, uint Id) Rank(uint mapId, IReadOnlyDictionary<uint, string> names)
		{
			return (IsVariantMap(names.GetValueOrDefault(mapId, "")) ? 1 : 0, mapId);
		}

		// A variant copy of a real playfield (the RIVERS/AUDIO/"BA sounds"/"FPS test" duplicates, e.g. "Tortage Island
		// - RIVERS_AUDIO"). These are the only maps that legitimately duplicate another map's resurrection Guids; no
		// real gameplay playfield name carries these tokens, and the check only decides a same-Guid COLLISION, so a
		// mis-tagged audio/test map can never lose a point it doesn't share with a real map.
		private static bool IsVariantMap(string name)
		{
			return name.Contains("RIVERS", StringComparison.OrdinalIgnoreCase)
				|| name.Contains("AUDIO", StringComparison.OrdinalIgnoreCase)
				|| name.Contains("BA sounds", StringComparison.OrdinalIgnoreCase)
				|| name.Contains("FPS test", StringComparison.OrdinalIgnoreCase);
		}

		private static float Num(Match m, string name)
		{
			return float.TryParse(Attr(m, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
				? v
				: 0f;
		}

		public readonly record struct Result(int Maps, int Points, int Empty, int FetchFailed);
	}
}
