using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the client's bounded-area trigger volumes (RDB 1000053) that a SUBSYSTEM actually CONSUMES into
	// MapRegion + MapRegionVertex, and links each consumer to its region. The server has no RDB access, so geometry
	// the runtime needs has to come through here. Survey + format: docs/rdb/bounded-areas-1000053.md.
	//
	// Four consumers today, each replacing an approximation with the client's real volume:
	//   - RESURRECTION DISCOVERY: a `*_RezDiscovery` / `*_Discovery` region that CONTAINS one of our
	//     RespawnPoints is that point's discovery volume. 13 of them exist against a hardcoded 30-unit radius.
	//   - quest ReachArea REGIONS: a goal whose `TrailingCategory` is 51200 carries a bounded-area reference;
	//     LDB category 51200 (`bounded_areas.bounded_area`) turns `TrailingRefId` into the region's name, and that
	//     names a region in the goal's playfield.
	//   - area BANNERS: the sub-area whose boundary floats the top-of-screen name. Two independent
	//     matches, and where both fire they agree: the client's own name against the recovered banner text
	//     (AreaBannerNames) and - for the banners whose region carries an internal name - the captured firing
	//     position, geometrically.
	//   - LAVA HAZARDS: the volumes that kill a player who walks in. Only the GEOMETRY is imported here; what
	//     the hazard does is the MapHazard side table, which is ours to fill (see that table).
	//
	// only WANTED REGIONS are IMPORTED - about 140 of the table's 8,917. Bulk-importing thousands of named volumes
	// nothing reads is data that rots. Everything a consumer asked for and did not get is reported, never skipped
	// silently (importer_silent_skip_is_data_loss).
	//
	// Clean rebuild each run: the set is fully derived from the fixed client RDB, and the derived key means a
	// re-import reproduces the same ids, so faolan.sql does not churn.
	public static class RdbRegionImporter
	{
		// `bounded_areas.bounded_area` - the client's id -> name table for these regions, named by the category
		// descriptor table in the TDL1 language record. Plaintext (unlike the quest/dialogue categories).
		public const uint BoundedAreaNameCategory = 51200;

		// The id space above this is a variant playfield numbering (0x40000000 | <playfield>) that is not a Map.Id.
		private const uint VariantIdBase = 0x40000000;

		// A RESURRECTION-point discovery volume - the ground you walk into to discover the point.
		//
		// The client spells the family several ways and only two of them contain "discovery":
		// `BA_MountainGlade_RezDiscovery`, `skammestein_res`, `graveyard_res`, `Pin Pin Respad`,
		// `Resurrection POI F`, `RezBA`. Anchored on `(?<![a-z])` rather than `\b` throughout, because `_` is a word
		// character - `\brez` matches neither `zang_xin_camp_rezpad` nor `BA_MountainGlade_RezDiscovery`, which is
		// how the survey came to file the whole discovery family under quest-goal.
		//
		// The name is only the first of three gates, and on its own it is actively dangerous: `FrostFields_Discovery`
		// and `FotD_Discovery` are area discovery - the zone-codex mechanic, 157x185 and 114x74 - and each CONTAINS a
		// respawn point of the same name ("Frost Fields", "Field of the Dead"). A containment test alone happily links
		// them and widens those points' discovery from a 30-unit radius to a box 25x the size. That is a different
		// mechanic wearing a matching name, so the size gate below is what keeps it out, not the vocabulary.
		private static readonly Regex DiscoveryName = new Regex(@"resurrect|rezz|re[zs]pad|(?<![a-z])rez|(?<![a-z])res$|_res(?![a-z])|(?<![a-z])res(pawn|point|spot)",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// The footprint above which a resurrection-named volume is area discovery rather than a respawn pad. The pads
		// are 10x11 to 56x50; the two area regions are 157x185 and 114x74, so nothing sits near this line.
		private const double DiscoveryPadFootprint = 3000;

		// Lava by name. Anchored on the word so `LavaDamage-3` / `BA_lava_dmg1` / `LavaArea2` / `HazardLava` all match.
		private static readonly Regex LavaName = new Regex(@"lava", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// Names that mention lava but are not a damage volume: the ambient audio/music companion regions. These also
		// carry a non-zero scryid (a client script), which is the independent check applied below - the name test is
		// belt-and-braces for an export that forgot the scryid.
		private static readonly Regex NotALavaVolume = new Regex(@"music|sfx|reverb|reveb|ambient|^camp\s*\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// Lava's decoded behaviour, from chat-annotated-4's death on Ymir's Pass: apply Incineration (3773837, 3 s) and
		// kill. Lethal rather than a damage number because retail sends no StatDamage at all for that death.
		private const uint IncinerationSpellId = 3773837;

		// The no-mount volumes. The client spells the family three ways and every one of them says the same
		// thing - `HeavensLake_DismountArea`, `BA_NoMount_Fengdu`, `No_mount_shaulun` - so this is a naming test whose
		// vocabulary is closed, not a guess at a purpose. Anchored so a bare `mount` inside a word cannot match, since
		// half the world's regions mention mountains.
		//
		// The anchor is `(?<![a-z])`, not `\b`: `_` is a word character, so `\bnomount` does not match
		// `BA_NoMount_Fengdu` - it silently found 11 of the 14 volumes and dropped all three of Kara Korum's. This is
		// the exact trap BoundedAreaClassifier's resurrection rule already carries a comment about.
		private static readonly Regex NoMountName = new Regex(@"dismount|(?<![a-z])no.?mount", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBaseUrl, IReadOnlyList<CapturedBanner> captured)
		{
			// The CDN base matters: 274 of the 478 bounded-area records are on-demand (not in the local.rdbdata
			// files), and Ymir's Pass is one of them. Without it CanRead rejects them and the import silently finds no
			// lava at all.
			using var rdb = new RdbReader(rdbDir, cdnBaseUrl);

			var notes = new List<string>();
			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var areasByMap = ReadRegions(rdb, ourMaps, notes);
			var names = BoundedAreaNames(rdb);

			// The wanted set, keyed by (map, name) so two consumers asking for the same region share one row.
			var wanted = new Dictionary<(uint Map, string Name), BoundedAreaCollection.BoundedArea>();

			var points = await db.RespawnPoints.ToListAsync();
			var discoveryFor = MatchDiscoveryVolumes(points, areasByMap, wanted, notes);

			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == BoundedAreaNameCategory && g.TrailingRefId > 0)
				.ToListAsync();
			var regionFor = MatchGoalRegions(goals, names, areasByMap, wanted, notes);

			// The banners, from both directions. The client's own recovery runs first: it reaches playfields
			// no capture has visited, and its match is an identity rather than an inference. What is left over goes to
			// the geometric route, which is the only one that can reach a banner whose region carries an internal name.
			//
			// Nothing about a banner is read from the database here - `captured` is the harvest, in memory. The
			// banner ends up as a column on the region that fires it (MapRegion.AreaNameId), so the captured sightings
			// are an input to this run rather than state it updates: the (playfield, id) pairs pin each playfield's id
			// numbering, and the hulls are the geometric route's only handle.
			var recovered = AreaBannerNames.Recover(rdb);
			var capturedByMap = captured
				.GroupBy(b => b.MapId)
				.ToDictionary(g => g.Key, g => (IReadOnlyCollection<uint>)g.Select(b => b.AreaNameId).ToList());

			var byName = AreaBannerNames.MatchRegions(areasByMap, recovered.Banners, capturedByMap, notes);
			foreach (var match in byName)
				wanted[(match.MapId, match.RegionName)] = areasByMap[match.MapId].First(a => a.Name == match.RegionName);

			// Only the captured banners the name join did not already place are matched geometrically - a banner the
			// name join placed is already on the region that bears its name.
			var placedIds = byName.Select(m => (m.MapId, m.AreaNameId)).ToHashSet();
			var byGeometry = MatchBannerRegions(
				captured.Where(b => !placedIds.Contains((b.MapId, b.AreaNameId))).ToList(),
				captured, placedIds, areasByMap, wanted, notes);

			var hazards = MatchLavaVolumes(areasByMap, wanted, notes);
			var noMount = MatchNoMountVolumes(areasByMap, wanted);

			// ---- Rebuild the region rows ----
			// Clear the FKs first: both are SetNull, but EF will not reorder the delete for us, and a stale link to a
			// row that is about to be removed is exactly the kind of thing that trips the SQLite FK check on rebuild.
			foreach (var p in points)
				p.DiscoveryRegionId = null;
			foreach (var g in goals)
				g.RegionId = null;
			await db.SaveChangesAsync();

			// The hazards cascade with their region, but EF only cascades what it has LOADED - clearing them here
			// makes the rebuild independent of whether the provider enforces the FK.
			db.MapHazards.RemoveRange(db.MapHazards);
			db.MapRegions.RemoveRange(db.MapRegions);
			await db.SaveChangesAsync();

			var vertices = 0;
			foreach (var ((mapId, name), area) in wanted)
			{
				var region = new MapRegion
				{
					Id = MapRegion.ComputeId(mapId, name),
					MapId = mapId,
					Name = name,
					MinY = area.MinY,
					MaxY = area.MaxY,
					MinX = area.Vertices.Min(v => v.X),
					MaxX = area.Vertices.Max(v => v.X),
					MinZ = area.Vertices.Min(v => v.Y),
					MaxZ = area.Vertices.Max(v => v.Y),
					ScryId = area.ScryId
				};

				for (var i = 0; i < area.Vertices.Count; i++)
				{
					region.Vertices.Add(new MapRegionVertex
					{
						Sequence = i,
						X = area.Vertices[i].X,
						Z = area.Vertices[i].Y
					});
				}

				db.MapRegions.Add(region);
				vertices += area.Vertices.Count;
			}

			await db.SaveChangesAsync();

			// ---- Link the consumers ----
			foreach (var (point, key) in discoveryFor)
				point.DiscoveryRegionId = MapRegion.ComputeId(key.Map, key.Name);
			foreach (var (goal, key) in regionFor)
				goal.RegionId = MapRegion.ComputeId(key.Map, key.Name);

			// ---- The area banners, as a column on the volume that fires them ----
			// A banner found by both routes records the agreement rather than either half: the two share no evidence at
			// all, one being a position track against a wire packet and the other a hash inversion against a string
			// table, so where they land on the same id that is the strongest thing the table can say.
			var capturedIds = captured.Select(b => (b.MapId, b.AreaNameId)).ToHashSet();
			var regionsById = await db.MapRegions.ToDictionaryAsync(r => r.Id);
			foreach (var (mapId, areaNameId, key, source) in
			         byName.Select(m => (m.MapId, m.AreaNameId, (Map: m.MapId, Name: m.RegionName),
					         capturedIds.Contains((m.MapId, m.AreaNameId)) ? AreaBannerSource.Corroborated : AreaBannerSource.ClientName))
				         .Concat(byGeometry.Select(g => (g.Banner.MapId, g.Banner.AreaNameId, g.Key, AreaBannerSource.Captured))))
			{
				var region = regionsById[MapRegion.ComputeId(key.Map, key.Name)];
				region.AreaNameId = areaNameId;
				region.AreaNameSource = source;
				_ = mapId;
			}

			// Every captured banner this run could not put on a volume is named, with its playfield. There is nowhere
			// else for one to live - the banner is a property of the region that fires it - so a banner without a
			// region is dropped, and saying which is the difference between a known gap and a silent one.
			var placed = byName.Select(m => (m.MapId, m.AreaNameId)).Concat(byGeometry.Select(g => (g.Banner.MapId, g.Banner.AreaNameId))).ToHashSet();
			var dropped = captured.Where(b => !placed.Contains((b.MapId, b.AreaNameId))).ToList();
			foreach (var b in dropped)
				notes.Add($"captured banner {b.AreaNameId} on map {b.MapId}: no client volume on that playfield is named after it and its sample separates it from none - DROPPED, the wire saw it but nothing can say where");

			foreach (var key in noMount)
				regionsById[MapRegion.ComputeId(key.Map, key.Name)].NoMount = true;

			// ---- The hazard BEHAVIOUR side table ----
			// Written after the regions exist, because it is keyed by the region's own id: what a volume does is ours
			// to say, where it is belongs to the client.
			foreach (var key in hazards)
			{
				db.MapHazards.Add(new MapHazard
				{
					RegionId = MapRegion.ComputeId(key.Map, key.Name),
					EffectSpellId = IncinerationSpellId,
					Damage = 0,
					Lethal = true
				});
			}

			await db.SaveChangesAsync();
			return new Result(wanted.Count, vertices, discoveryFor.Count, regionFor.Count, byName.Count + byGeometry.Count,
				byGeometry.Count, dropped.Count, recovered, hazards.Count, noMount.Count, notes);
		}

		// Every parseable 1000053 record for a playfield we host, as map -> the regions with a testable footprint. A
		// record we cannot read is reported: a hazard family that quietly went missing because its playfield failed to
		// decompress looks exactly like a family the client does not ship.
		private static Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> ReadRegions(RdbReader rdb, HashSet<uint> ourMaps, List<string> notes)
		{
			var byMap = new Dictionary<uint, List<BoundedAreaCollection.BoundedArea>>();
			var unreadable = 0;
			foreach (var e in rdb.EntriesOfType(BoundedAreaCollection.ResourceType).Where(rdb.CanRead).OrderBy(e => e.Id))
			{
				// The id space above VariantIdBase is a variant playfield numbering (0x40000000 | <playfield>) that is
				// not a Map.Id. Skipped rather than masked: nothing shows the variant is the same content, and masking
				// would silently merge two records into one region set.
				if (e.Id >= VariantIdBase || !ourMaps.Contains(e.Id))
					continue;

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

				byMap[e.Id] = collection.Areas.Where(a => a.IsPolygon).ToList();
			}

			if (unreadable > 0)
				notes.Add($"{unreadable} bounded-area record(s) for playfields we host would not read - every region on them is missing, not absent");

			return byMap;
		}

		// The LAVA volumes. Only lava, and that is a deliberate limit rather than an oversight: the same
		// records hold hundreds of other hazard-shaped names (`TrapBA`, `BA_SpikeTrap`, `SnakePitBA`,
		// `PlayerDrowns_Dies`, `BA_PoisonFog`, `BADeathBridge`,...) whose behaviour nothing in the corpus shows.
		// Importing them means shipping hundreds of GUESSED kill volumes, and one wrong guess silently kills players
		// on a map nobody is testing, which is strictly worse than the feature being absent. Lava is in because it is
		// the family the wire actually pins (see MapHazard).
		//
		// Every REJECTED lava-named area is reported with its reason. A silent `continue` in an importer is data loss
		// you find out about months later: if a future client export moves the damage volumes behind a scryid, these
		// lines are what say so.
		private static List<(uint Map, string Name)> MatchLavaVolumes(
			Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			Dictionary<(uint, string), BoundedAreaCollection.BoundedArea> wanted,
			List<string> notes)
		{
			var matched = new List<(uint, string)>();
			foreach (var (mapId, areas) in areasByMap.OrderBy(kv => kv.Key))
			{
				foreach (var area in areas)
				{
					if (!LavaName.IsMatch(area.Name))
						continue;

					if (area.ScryId != 0)
					{
						notes.Add($"lava '{area.Name}' on map {mapId}: scryid {area.ScryId} = a CLIENT script (ambient/audio region), not a damage volume");
						continue;
					}

					if (NotALavaVolume.IsMatch(area.Name))
					{
						notes.Add($"lava '{area.Name}' on map {mapId}: name is an ambient/label region, not a damage volume");
						continue;
					}

					if (float.IsNaN(area.MinY) || float.IsNaN(area.MaxY))
					{
						notes.Add($"lava '{area.Name}' on map {mapId}: no <heights> range, so the prism is unbounded vertically");
						continue;
					}

					wanted[(mapId, area.Name)] = area;
					matched.Add((mapId, area.Name));
				}
			}

			return matched;
		}

		// The volumes riding is forbidden in. No behaviour to decide - a no-mount volume has no parameters, so
		// unlike the lava there is nothing here that could be guessed wrong; the only question is whether the name
		// means what it says, and this family's vocabulary is closed.
		//
		// Taken at any scryid, unlike lava. Four of the fourteen run the crossing NOTIFIER (220656) - the client
		// telling the server the player entered - which is evidence FOR the family rather than against it, and the
		// other ten run nothing at all. A hazard's `scryid != 0` test excludes ambient audio TWINS of a damage volume;
		// there is no such thing as an ambient twin of a no-mount area.
		private static List<(uint Map, string Name)> MatchNoMountVolumes(
			Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			Dictionary<(uint, string), BoundedAreaCollection.BoundedArea> wanted)
		{
			var matched = new List<(uint, string)>();
			foreach (var (mapId, areas) in areasByMap.OrderBy(kv => kv.Key))
			foreach (var area in areas.Where(a => NoMountName.IsMatch(a.Name)))
			{
				wanted[(mapId, area.Name)] = area;
				matched.Add((mapId, area.Name));
			}

			return matched;
		}

		// LDB category 51200: the bounded-area id -> name table, from the english pages. Language matters even for
		// these short names, and taking "whatever page came first" silently yields French.
		private static Dictionary<uint, string> BoundedAreaNames(RdbReader rdb)
		{
			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);
			var names = new Dictionary<uint, string>();
			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(rdb.CanRead))
			{
				if (languageByPage.GetValueOrDefault(e.Id) != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (LdbStringIndex.CategoryOf(body) != BoundedAreaNameCategory)
					continue;

				foreach (var entry in LdbStringIndex.Parse(body))
					names[entry.Id] = entry.Value;
			}

			return names;
		}

		// A respawn point's discovery volume: the smallest region containing it that passes all three gates.
		//
		//   1. name - the resurrection family's own vocabulary (see DiscoveryName).
		//   2. SIZE - a respawn PAD, not the zone-codex area that happens to contain the same point.
		//   3. SCRYID 0 - server-owned behaviour, which is what discovery is. All 11 volumes the narrower `rezdiscov`
		//      rule found carry it; the 7 pad-sized ones that do not are running a client script, and the same
		//      principle that keeps `BA_Lava_music` out of the hazard import keeps them out of this one.
		//
		// Smallest because the pads nest inside larger audio/collision volumes that happen to share the naming, and
		// the tightest match is the one the designer drew around the point.
		//
		// Widened from `rezdiscov` alone, which reached 11 of 538 points. The value it replaces is a FEEL
		// call and known to be wrong in a specific direction: `RespawnDiscoveryRange` is a 60x60 box with no vertical
		// bound, against real volumes of 16x20 to 29x33 and ~10 units tall, so we discover a point from roughly twice
		// retail's distance and from any height. The RDB carries no radius at all, so these volumes are the only
		// source there is.
		//
		// It is an inference from name + shape + scryid + containment, not from a name that says discovery -
		// `Pin Pin Respad` may be the shrine's footprint rather than its trigger. Three signals agreeing is decent
		// evidence and the failure mode is bounded: a slightly wrong discovery radius, against a value already known
		// to be too generous.
		private static List<(RespawnPoint Point, (uint Map, string Name) Key)> MatchDiscoveryVolumes(
			List<RespawnPoint> points,
			Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			Dictionary<(uint, string), BoundedAreaCollection.BoundedArea> wanted,
			List<string> notes)
		{
			var matched = new List<(RespawnPoint, (uint, string))>();
			foreach (var p in points)
			{
				if (!areasByMap.TryGetValue(p.MapId, out var areas))
					continue;

				BoundedAreaCollection.BoundedArea? best = null;
				var bestArea = double.MaxValue;
				foreach (var a in areas)
				{
					if (a.ScryId != 0 || !DiscoveryName.IsMatch(a.Name) || !a.Contains(p.PositionX, p.PositionZ))
						continue;

					var box = (a.Vertices.Max(v => v.X) - a.Vertices.Min(v => v.X)) * (double)(a.Vertices.Max(v => v.Y) - a.Vertices.Min(v => v.Y));
					if (box > DiscoveryPadFootprint || box >= bestArea)
						continue;

					best = a;
					bestArea = box;
				}

				if (best == null)
					continue;

				wanted[(p.MapId, best.Name)] = best;
				matched.Add((p, (p.MapId, best.Name)));
			}

			// A pad that discovers two points is either one pad serving both or the wrong pad for one of them, and
			// nothing here can tell which - so it is reported rather than quietly shipped.
			foreach (var shared in matched.GroupBy(m => m.Item2).Where(g => g.Count() > 1))
				notes.Add($"discovery volume '{shared.Key.Item2}' on map {shared.Key.Item1} is the tightest match for {shared.Count()} respawn points ({string.Join(", ", shared.Select(m => m.Item1.Name))})");

			return matched;
		}

		// A captured banner's trigger volume. The harvest gives a hull over the positions retail actually
		// fired each banner at, which under-covers by construction; this finds the client region that hull sits in.
		//
		// The route of last resort, and still the only one for a banner whose region carries an INTERNAL name: Old
		// Tarantia's volumes are `BA_Bridge`/`BA_EastGate`, never the "Bridge of Vilerus I" the banner renders, so the
		// name join cannot reach them and geometry is all there is. Where both routes fire they agree - which is
		// the cross-check neither could give itself - so this only runs on what the name join left over.
		//
		// Matched geometrically, i.e. on the position rather than the text. Three gates, all of which have to hold:
		//
		//   1. The region runs the CROSSING-NOTIFIER scry script. A volume that does not tell the server the player
		//      entered it cannot be what fires a server-sent banner, and this is what excludes the audio regions,
		//      building interiors and playfield borders that containment alone happily returns. It takes Old
		//      Tarantia from "four containing regions, tightest `City_sounds` at 142,490 u²" to exactly one.
		//   2. It contains the banner's captured footprint.
		//   3. It SEPARATES this banner from every other one on the map. A region holding two banners' samples
		//      cannot be what distinguishes them, so it is not the trigger for either.
		//
		// Deliberately no tolerance on the containment test. A banner fires on entry, so its sample sits right at
		// the boundary and a near-miss is expected. Padding the test until they match would be fitting the answer -
		// and a banner that reaches no volume is dropped rather than kept on its hull, so the cost of padding would
		// be wrong areas rather than missing ones.
		private static List<(CapturedBanner Banner, (uint Map, string Name) Key)> MatchBannerRegions(
			List<CapturedBanner> unplaced,
			IReadOnlyList<CapturedBanner> allBanners,
			HashSet<(uint, uint)> placedByName,
			Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			Dictionary<(uint, string), BoundedAreaCollection.BoundedArea> wanted,
			List<string> notes)
		{
			var matched = new List<(CapturedBanner, (uint, string))>();
			foreach (var byMap in unplaced.GroupBy(b => b.MapId))
			{
				if (!areasByMap.TryGetValue(byMap.Key, out var areas))
					continue;

				// The separation gate is measured against every banner on the playfield that is still ARGUING for a
				// region - not just the unplaced ones, and not the ones an identity has already settled.
				//
				// Both halves of that are load-bearing and each was wrong once. Scoping it to the unplaced banners
				// let a region that swallows another banner's samples through. Widening it to all banners then broke
				// the opposite way: on map 1010 the region `Fifo-CimmerianSettlement` holds the sample of the banner
				// the name join pinned to `Fifo-PasstoConarchVillage` 600 units away - and since two banners cannot
				// fire from one spot, that sample is what is wrong, not the region. A banner whose region is already
				// known by name does not get a veto: its stray sample says the sample is imprecise, which is exactly
				// what a one-sighting hull is, and never that some other region is ambiguous.
				var siblings = allBanners
					.Where(b => b.MapId == byMap.Key && !placedByName.Contains((b.MapId, b.AreaNameId)))
					.ToList();
				foreach (var banner in byMap)
				{
					var cx = banner.CentreX;
					var cz = banner.CentreZ;

					BoundedAreaCollection.BoundedArea? best = null;
					var bestBox = double.MaxValue;
					foreach (var a in areas)
					{
						if (a.ScryId != BoundedAreaClassifier.CrossingNotifierScryId || !a.Contains(cx, cz))
							continue;

						// Gate 3: it must not also hold another banner on this map.
						if (siblings.Any(o => o.AreaNameId != banner.AreaNameId && a.Contains(o.CentreX, o.CentreZ)))
							continue;

						var box = (a.Vertices.Max(v => v.X) - a.Vertices.Min(v => v.X)) * (double)(a.Vertices.Max(v => v.Y) - a.Vertices.Min(v => v.Y));
						if (box >= bestBox)
							continue;

						best = a;
						bestBox = box;
					}

					if (best == null)
						continue; // reported once, by the DROPPED note the caller writes

					wanted[(banner.MapId, best.Name)] = best;
					matched.Add((banner, (banner.MapId, best.Name)));
				}
			}

			return matched;
		}

		// A quest goal's region: LDB[51200][TrailingRefId] gives the name, then find it on the goal's playfield.
		// A goal with no MapId still resolves when the name occurs on exactly one playfield we host (goal 14073's
		// `BossRoom` is only on 2027) - ambiguity is reported rather than guessed.
		private static List<(QuestGoalDefinition Goal, (uint Map, string Name) Key)> MatchGoalRegions(
			List<QuestGoalDefinition> goals,
			Dictionary<uint, string> names,
			Dictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			Dictionary<(uint, string), BoundedAreaCollection.BoundedArea> wanted,
			List<string> notes)
		{
			var matched = new List<(QuestGoalDefinition, (uint, string))>();
			foreach (var g in goals)
			{
				if (!names.TryGetValue(g.TrailingRefId, out var name))
				{
					notes.Add($"goal {g.GoalId}: LDB[{BoundedAreaNameCategory}][{g.TrailingRefId}] has no English entry");
					continue;
				}

				var candidates = (g.MapId is { } mid
						? areasByMap.TryGetValue(mid, out var onMap)
							? onMap.Where(a => a.Name == name).Select(a => (Map: mid, Area: a))
							: []
						: areasByMap.SelectMany(kv => kv.Value.Where(a => a.Name == name).Select(a => (Map: kv.Key, Area: a))))
					.ToList();

				if (candidates.Count == 0)
				{
					notes.Add($"goal {g.GoalId}: region '{name}' not found on {(g.MapId is { } m ? $"map {m}" : "any hosted playfield")}");
					continue;
				}

				if (candidates.Count > 1)
				{
					notes.Add($"goal {g.GoalId}: region '{name}' is ambiguous - on maps {string.Join(", ", candidates.Select(c => c.Map).Distinct())}");
					continue;
				}

				var (map, area) = candidates[0];
				wanted[(map, name)] = area;
				matched.Add((g, (map, name)));
			}

			return matched;
		}

		public readonly record struct Result(
			int Regions,
			int Vertices,
			int DiscoveryLinks,
			int GoalLinks,
			int Banners,
			int GeometricBanners,
			int DroppedBanners,
			AreaBannerNames.Recovery Recovered,
			int Hazards,
			int NoMount,
			IReadOnlyList<string> Notes);
	}
}
