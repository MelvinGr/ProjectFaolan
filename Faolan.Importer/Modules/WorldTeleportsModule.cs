using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// Zone-line teleport destinations: enriches the DoorFullUpdateIIR world objects that are teleporters with their
	// destination (map + arrival), harvested from captured UseObject->N3Teleport sequences (WorldTeleportExtractor).
	// A door actually walked through in a capture gets its destination whole (map + arrival position); the rest are
	// then resolved by name against the client's playfield table (ZoneLineNames), which yields the destination
	// map but no arrival - the runtime falls back to that map's default spawn. Whatever neither pass can place keeps
	// InteractAction=None. Depends on world-objects (the rows it enriches must exist).
	public sealed class WorldTeleportsModule : IImportModule
	{
		private const float PositionTolerance = 1.0f; // burst pos vs stored row pos (both from the same import)

		// how far the client table's teleport trigger point may sit from the door OBJECT's own position. Wider
		// than PositionTolerance on purpose - those two are the same import's coordinates for the same thing, these
		// are two different things (the trigger sits in front of the door). Measured: 58% of matchable rows land
		// within 0.5 u, 90.9% within 8 u, and the tail past that is a different door.
		private const float ClientTableTolerance = 8f;

		// A runner-up this much closer than the best (plus half a unit of slack) makes the pick ambiguous.
		private const float AmbiguityRatio = 1.5f;

		// No hard DependsOn (mirrors NpcDispositionModule): it enriches whatever zone-line WorldObjects already exist,
		// and registration order already sequences it right after `world-objects` in a full default run. A hard dep
		// would drag the heavy world-objects re-import into a targeted `--only world-teleports` run - which fails on a
		// populated DB (its Insert path collides on the (map,pos,name) unique key). On an empty DB this just no-ops.
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Zone-line teleport destinations (captured UseObject->N3Teleport).";
		public string Name => "world-teleports";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var links = WorldTeleportExtractor.Merge(ctx.Captures.SelectMany(c => WorldTeleportExtractor.Extract(c.Packets)));

			// Overwrite: clear any previously-written destinations so a re-import reflects only the current harvest.
			var cleared = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite)
			{
				foreach (var o in await ctx.Db.WorldObjects.Where(o => o.InteractAction != InteractAction.None).ToListAsync())
				{
					o.InteractAction = InteractAction.None;
					o.DestinationMapId = null;
					o.DestinationX = o.DestinationY = o.DestinationZ = null;
					o.DestinationPlayfieldClass = null;
					cleared++;
				}
			}

			// Enrich both door classes the extractor harvests: DoorFullUpdateIIR zone-lines and the SimpleDynel doors
			// (e.g. the Thirsty Dog Inn) - WorldTeleportExtractor.DoorSubOpcodes.
			var zoneLines = await ctx.Db.WorldObjects.Where(o => WorldTeleportExtractor.DoorSubOpcodes.Contains(o.SubOpcode)).ToListAsync();

			var matched = 0;
			var misses = 0;
			foreach (var link in links)
			{
				var row = NearestZoneLine(zoneLines, link);
				if (row == null)
				{
					misses++;
					continue;
				}

				// Same-playfield reposition vs cross-playfield handoff - the client's Teleport vs ProxyTeleport GameAction.
				row.InteractAction = link.DestMapId == row.MapId
					? InteractAction.Teleport
					: InteractAction.ProxyTeleport;
				row.DestinationMapId = link.DestMapId;
				row.DestinationX = link.DestX;
				row.DestinationY = link.DestY;
				row.DestinationZ = link.DestZ;
				// non-zero only when the server answered this door with an ACG bind box. Stored as the wire
				// class word rather than a bool - see WorldObject.DestinationPlayfieldClass for why.
				row.DestinationPlayfieldClass = link.AcgPlayfieldClass == 0
					? null
					: link.AcgPlayfieldClass;
				matched++;
			}

			// tier 2: the client's own teleport table. Runs after the capture harvest and before the name
			// fallback, which is exactly its standing: better than guessing from a door's name, worse than a
			// capture (it has no arrival position, and the capture is what retail actually did).
			var (fromRdb, rdbAmbiguous) = ResolveFromClientTable(ctx, zoneLines);

			var (named, ambiguous) = await ResolveByNameAsync(ctx, zoneLines);
			var confirms = await WriteTravelConfirmsAsync(ctx, links);

			await ctx.Db.SaveChangesAsync();

			var prefix = cleared > 0
				? $"cleared {cleared}; "
				: "";
			return new ImportResult($"{prefix}{links.Count} teleport links harvested, {matched} matched to world objects, {misses} unmatched; " +
				$"{fromRdb} more from the client's teleport table, {rdbAmbiguous} skipped as ambiguous (#883); " +
				$"{named} more resolved by name against the playfield table, {ambiguous} named but unresolved (#341); " +
				$"{confirms} destination map(s) carry a travel confirmation box (#637).");
		}

		// the "Do you want to travel to <map>?" confirmation box, scoped to the DESTINATION map (Map.TravelConfirmKey
		// says why). A harvested link whose capture showed a box between the use and the teleport marks its destination
		// map as confirming, and carries the localized key the box named it with.
		//
		// A link with no box does not clear a key - "this walk-through showed no box" and "this map never confirms" are
		// different statements, and only `--mode overwrite` (which clears above) is entitled to the second. A conflict
		// (two links to one map disagreeing on the key) would be the signal that this is per-door after all; none is
		// observed, so the first key wins and the disagreement is logged rather than silently resolved.
		private static async Task<int> WriteTravelConfirmsAsync(ImportContext ctx, IReadOnlyList<TeleportLink> links)
		{
			var keys = new Dictionary<uint, ProtoDecryptionKey>();
			foreach (var link in links)
			{
				if (LdbKey.Decode(link.ConfirmKey) is not { } key)
					continue;

				if (keys.TryGetValue(link.DestMapId, out var existing) && !Equals(LdbKey.Encode(existing), LdbKey.Encode(key)))
				{
					Console.WriteLine($"  ⚠️ map {link.DestMapId} has two different travel-confirm keys across captures - keeping the first (#637).");
					continue;
				}

				keys.TryAdd(link.DestMapId, key);
			}

			var written = 0;
			foreach (var map in await ctx.Db.Maps.Where(m => keys.Keys.Contains(m.Id)).ToListAsync())
			{
				map.TravelConfirmKey = keys[map.Id];
				written++;
			}

			return written;
		}

		// the doors no capture walked through. Their name is the playfield they lead to, so the client's own
		// playfield table resolves them (ZoneLineNames) - map only, never an arrival position, which stays wire-only
		// truth. Runs after the capture harvest and only over rows it left inert, so a harvested destination always
		// wins. Doors whose name matches no playfield are left alone: a "Lever" and a zone-line to a playfield the
		// client does not ship are indistinguishable from here, and marking them would make every lever complain.
		private static async Task<(int Named, int Ambiguous)> ResolveByNameAsync(ImportContext ctx, IReadOnlyList<WorldObject> zoneLines)
		{
			var maps = await ctx.Db.Maps.Select(m => new
			{
				m.Id,
				m.Name
			}).ToListAsync();
			var withSpawn = (await ctx.Db.RespawnPoints.Select(r => r.MapId).Distinct().ToListAsync()).ToHashSet();

			var mapNames = maps.ToDictionary(m => m.Id, m => m.Name);
			var byName = maps
				.Where(m => !string.IsNullOrWhiteSpace(m.Name))
				.GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => (IReadOnlyList<ZoneLineNames.MapCandidate>)g
					.Select(m => new ZoneLineNames.MapCandidate(m.Id, withSpawn.Contains(m.Id)))
					.ToList(), StringComparer.OrdinalIgnoreCase);

			int named = 0, ambiguous = 0;
			foreach (var row in zoneLines)
			{
				if (row.InteractAction != InteractAction.None)
					continue;

				var key = ZoneLineNames.Key(row.Name);
				if (key == null || !byName.TryGetValue(key, out var candidates))
					continue;

				if (ZoneLineNames.LeadsBackIntoOwnPlayfield(key, mapNames.GetValueOrDefault(row.MapId)))
					continue;

				var dest = ZoneLineNames.PickDestination(candidates);
				if (dest == row.MapId)
					continue;

				// Cross-playfield by construction - the same-playfield cases are the two `continue`s above. A null
				// destination is deliberate on a tie: the row is a zone-line (its name is a playfield), so the server
				// should say it has no known destination instead of playing the door-open animation at it.
				row.InteractAction = InteractAction.ProxyTeleport;
				row.DestinationMapId = dest;
				row.DestinationX = row.DestinationY = row.DestinationZ = null;
				if (dest == null)
					ambiguous++;
				else
					named++;
			}

			return (named, ambiguous);
		}

		// The stored zone-line row for a harvested link: same map + name, nearest to the captured source position
		// (a name can repeat across maps; within a map the position disambiguates). Null when none is within tolerance.
		// tier 2 - the client's own complete teleport table (RDB 1000087). The capture harvest can only ever see
		// a door somebody walked through on camera (160 links over 24 maps); this table states all 675 outright.
		//
		// PRECEDENCE, and each step of it is measured rather than assumed (`--report teleport-match`):
		//   * a captured link always wins. It carries the arrival position, which this table does not have at all,
		//     and it is what retail actually did. The two differ in a way that matters: the table is a design-time
		//     export naming a canonical destination, and the runtime picks a variant of it. Of the 45 doors where both
		//     sources speak, 41 agree and the 4 that do not are day/night or state variants of the same place -
		//     `Renton's House` (4572) vs `Renton's House Night` (4582), `Villa Camillus` vs `Villa Camillus - Cursed`.
		//     So the table is right about which place and the capture is right about which instance of it.
		//   * this table beats the name fallback, which is why it runs first. Two of those four disagreements are
		//     against a name guess, and on `City of Tortage Night` the table's `Renton's House Night` is plainly the
		//     better answer than the name match's daytime `Renton's House`.
		//
		// The match is position-only - the table carries no door name, so `NearestZoneLine`'s name check is not
		// available here. Guarded two ways: a hard 8 u cap (90.9% of measurable rows land inside it, 58% inside 0.5 u)
		// and a runner-up test, because the danger of a position-only key is picking the neighbouring door rather than
		// missing. Nothing is written for a row that already has a destination.
		//
		// Only 88 of the 675 rows can be matched at all today, because 587 are on maps with no imported world
		// objects. That is the world data being incomplete, not a defect here - the yield grows on its own as maps land.
		private static (int Resolved, int Ambiguous) ResolveFromClientTable(ImportContext ctx, IReadOnlyList<WorldObject> zoneLines)
		{
			if (string.IsNullOrWhiteSpace(ctx.RdbDir) || !Directory.Exists(ctx.RdbDir))
			{
				ctx.Out.WriteLine("  (no client RDB configured - skipping the client teleport table, #883)");
				return (0, 0);
			}

			using var rdb = new RdbReader(ctx.RdbDir, ctx.RdbProxyBase);
			var table = RdbTeleportExtractor.Extract(rdb);
			if (table.Count == 0)
			{
				ctx.Out.WriteLine($"  ⚠️ RDB {RdbTeleportExtractor.ResourceType}:{RdbTeleportExtractor.RecordId} is not readable in this client - no teleport table (#883).");
				return (0, 0);
			}

			// Only rows the capture pass left inert are candidates; a harvested destination always wins.
			var byMap = zoneLines.Where(o => o.InteractAction == InteractAction.None)
				.GroupBy(o => o.MapId)
				.ToDictionary(g => g.Key, g => g.ToList());

			int resolved = 0, ambiguous = 0;
			foreach (var teleport in table)
			{
				if (!byMap.TryGetValue(teleport.SourceMapId, out var candidates))
					continue;

				var ranked = candidates.Select(o => (Door: o, Dist: Distance(o, teleport))).OrderBy(r => r.Dist).ToList();
				if (ranked[0].Dist > ClientTableTolerance)
					continue;

				// A position-only key fails by picking the NEIGHBOUR, not by missing - so when a second door is
				// nearly as close, decline rather than choose. Zero rows hit this on the current data; it is here
				// because the guard's absence would be invisible until it was wrong.
				if (ranked.Count > 1 && ranked[1].Dist <= ranked[0].Dist * AmbiguityRatio + 0.5f)
				{
					ambiguous++;
					continue;
				}

				var row = ranked[0].Door;
				row.InteractAction = teleport.DestMapId == row.MapId
					? InteractAction.Teleport
					: InteractAction.ProxyTeleport;
				row.DestinationMapId = teleport.DestMapId;

				// no arrival: the table does not carry one, and the runtime already falls back to the destination
				// map's default spawn for exactly this case. Writing an invented position would be the worse bug.
				row.DestinationX = row.DestinationY = row.DestinationZ = null;
				resolved++;
			}

			return (resolved, ambiguous);
		}

		private static float Distance(WorldObject o, RdbTeleport t)
		{
			var dx = o.PositionX - t.X;
			var dy = o.PositionY - t.Y;
			var dz = o.PositionZ - t.Z;

			return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
		}

		private static WorldObject? NearestZoneLine(IReadOnlyList<WorldObject> zoneLines, TeleportLink link)
		{
			WorldObject? best = null;
			var bestSq = PositionTolerance * PositionTolerance;
			foreach (var o in zoneLines)
			{
				if (o.MapId != link.SourceMapId || !string.Equals(o.Name, link.SourceName, StringComparison.Ordinal))
					continue;

				var dx = o.PositionX - link.SrcX;
				var dy = o.PositionY - link.SrcY;
				var dz = o.PositionZ - link.SrcZ;
				var sq = dx * dx + dy * dy + dz * dz;
				if (sq <= bestSq)
				{
					bestSq = sq;
					best = o;
				}
			}

			return best;
		}
	}
}
