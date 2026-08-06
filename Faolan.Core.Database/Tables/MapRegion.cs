using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A named TRIGGER VOLUME on a playfield, imported verbatim from the client's bounded-area table (RDB type
	// 1000053 = BoundedAreaCollection). A prism: the `Vertices` (x, z) ring between MinY and MaxY. Format and the
	// full survey of what is in that table: docs/rdb/bounded-areas-1000053.md.
	//
	// This exists because the SERVER HAS NO RDB ACCESS - it never constructs an RdbReader and is not going to - so
	// any client geometry the runtime needs has to be imported. The regions here are the ones a subsystem actually
	// consumes: resurrection-point discovery volumes (#519) and quest ReachArea goal regions (#521), with the area
	// banners (#348) the next candidate.
	//
	// ⚠️ ONLY REGIONS WITH A CONSUMER ARE IMPORTED - about 140 of the table's 8,917, not the lot. A region is only as
	// meaningful as the behaviour attached to it, and bulk-importing thousands of named volumes nothing reads is data
	// that rots. RdbRegionImporter reports every region a consumer asked for and did not get, rather than skipping
	// silently.
	//
	// A DERIVED key rather than the natural (MapId, Name) composite: the vertex rows hang off this one, and carrying
	// a NAME string down into every one of them is what DerivedKey exists to avoid. The
	// id is a pure function of (MapId, Name) and the client RDB is fixed content, so a re-import reproduces it
	// exactly - and the nullable FKs that point here (RespawnPoint, QuestGoalDefinition) need a single column to
	// express SetNull, which a composite cannot.
	[Index(nameof(MapId), nameof(Name), IsUnique = true)]
	public class MapRegion : BaseEntity, IDerivedKey
	{
		public static uint ComputeId(uint mapId, string name)
		{
			return DerivedKey.From(mapId, name);
		}

		public uint ComputeId()
		{
			return ComputeId(MapId, Name);
		}

		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// The client's own name for the volume (`Trappers Haven`, `BA_HoCRezDiscovery`), verbatim from the RDB. Part
		// of the identity, and the handle everything else resolves through: LDB category 51200
		// (`bounded_areas.bounded_area`) maps a quest goal's region reference to exactly this string.
		public string Name { get; set; } = null!;

		// The prism's Y range, from the RDB `<heights>` pair. Load-bearing rather than decoration: the discovery
		// volumes are only ~10 units tall, so an (x, z)-only test would fire from the wrong floor wherever a region
		// is stacked over another - the same trap the lava prism's heights exist to avoid.
		public float MinY { get; set; }
		public float MaxY { get; set; }

		// The polygon's axis-aligned bounds, precomputed from the vertices. A CHEAP REJECT so a containment test can
		// skip the ring walk, never the test itself: several of these regions are rotated quads whose AABB is nearly
		// TWICE the real area (BA_MountainGlade_RezDiscovery is 249 u² inside a 495 u² box), so an AABB hit means
		// "worth walking" and nothing more.
		public float MaxX { get; set; }
		public float MaxZ { get; set; }
		public float MinX { get; set; }
		public float MinZ { get; set; }

		// The RDB `<scryid>`: non-zero means the CLIENT runs a script on entry, 0 means any behaviour is the
		// server's. Kept because it is the one field that distinguishes a server-owned volume from a client-owned
		// one without guessing from the name.
		public uint ScryId { get; set; }

		// The boundary polygon, in RDB order; cascade-delete with the region.
		public virtual ICollection<MapRegionVertex> Vertices { get; set; } = [];

		// The top-of-screen AREA BANNER this region floats when a player walks into it (#348/#513/#679/#680), or null
		// for the regions imported for something else. What goes on the wire, and nothing else: the CLIENT renders the
		// string from its own LDB, so there is deliberately no name column here - it would be a stale copy of
		// something the client answers exactly, in seven languages, from the id alone.
		//
		// ⚠️ The id is a PARAMETER inside the localized token, not an LDB key:
		//
		//   <localized category='50025' token='DisplayedText(3)|GenericDisplayAreaName|3474586'>
		//   elf28("DisplayedText(3)|GenericDisplayAreaName|3474586") == 19303910 == cat 50025 "Castle Approach"
		//
		// so searching the LDB for the number itself always fails, and that failure means nothing. It has been read as
		// meaning something twice. `--report ldb-strings --report-arg "id:#<elf28 of the token>"` resolves one;
		// `--report area-banner-names` does the whole table at once by INVERTING that hash.
		//
		// This used to be a table of its own, `MapArea`, keyed by (map, banner) and carrying a HULL over the positions
		// retail was captured firing the banner at - an approximation the runtime had to widen with a slack radius,
		// because a hull only covers ground a captured player actually walked. Once the recovery could name the
		// client's own volume for 140 of 142 banners the hull had nothing left to do, and a banner is simply a
		// property OF a trigger volume. See AreaNameSource for what happened to the last two.
		public uint? AreaNameId { get; set; }

		// How AreaNameId was arrived at - PROVENANCE, not confidence; null exactly when AreaNameId is. The two routes
		// share no evidence at all, which is why an agreement between them (`Corroborated`) is the strongest row in
		// the table and a disagreement would be the loudest thing either could say. Nothing disagrees.
		public AreaBannerSource? AreaNameSource { get; set; }

		// Whether riding is forbidden here (#681): a player who crosses in mounted is UNSEATED, and one inside cannot
		// mount. The client's own names for these are `HeavensLake_DismountArea`, `BA_NoMount_Fengdu`,
		// `No_mount_shaulun` - settlements and palaces, the places retail makes you walk.
		//
		// A FLAG rather than a MapHazard-style side table, and the difference is the point of that split: a hazard has
		// parameters to carry (which spell, how much damage, whether it kills) so it needs a row of its own, while a
		// no-mount volume has none at all - the behaviour is fully described by "here". A 1:1 table holding one
		// boolean would be a join to learn nothing.
		//
		// ⚠️ `Map.PlayfieldFlags` gates mounting per PLAYFIELD; this is the sub-playfield version, so a city inside a
		// mountable zone stops being rideable. The two are independent and both apply.
		public bool NoMount { get; set; }

		// What this region DOES to a player who enters it, where that is a hazard (#504/#679) - null for the ordinary
		// case, which is every region imported for a discovery volume, a quest goal or an area banner. Behaviour is a
		// 1:1 side table rather than columns here so that importing a volume's geometry never implies it hurts
		// anybody; see MapHazard.
		public virtual MapHazard? Hazard { get; set; }
	}
}
