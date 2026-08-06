using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
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
	// ⚠️ ONLY REGIONS WITH A CONSUMER ARE IMPORTED - about 34 of the table's 8,917, not the lot. That is the same
	// deliberate limit MapHazard draws for lava: a region is only as meaningful as the behaviour attached to it, and
	// bulk-importing thousands of named volumes nothing reads is data that rots. RdbRegionImporter reports every
	// region a consumer asked for and did not get, rather than skipping silently.
	//
	// ⚠️ Deliberately SEPARATE from MapHazard for now, which is the same shape (a named prism with a vertex ring)
	// plus its effect columns. Folding the two is the right end state - MapHazard becomes a satellite of this
	// holding only EffectSpellId/Damage/Lethal - but NOT while that table is the one piece of geometry proven
	// against a capture to 262 ms (MapHazardGeometryTests). Migrating 105 volumes and 5,396 vertices for tidiness
	// would risk the only wire-validated containment path we have. Do it when the hazard path is being touched anyway.
	//
	// A DERIVED key rather than the natural (MapId, Name) composite, for MapHazard's reason: the vertex rows hang
	// off this one, and carrying a NAME string down into every one of them is what DerivedKey exists to avoid. The
	// id is a pure function of (MapId, Name) and the client RDB is fixed content, so a re-import reproduces it
	// exactly - and the nullable FKs that point here (RespawnPoint, QuestGoalDefinition) need a single column to
	// express SetNull, which a composite cannot.
	[Index(nameof(MapId), nameof(Name), IsUnique = true)]
	// ⚠️ **MapHazard is slated to merge INTO this table (#679)**, with hazard behaviour moving to a 1:1 side table -
	// the two are the same 1000053 prism stored twice. `MapArea` folds in later still, as a nullable AreaNameId
	// column, once its rows come from the client and it stops carrying geometry of its own. Until then MapArea is a
	// CONSUMER of a region (like RespawnPoint and QuestGoalDefinition), not a kind of one: its bounds are a sampled
	// hull, and merging an approximation into the table of exact client polygons is the mistake MapHazard's own
	// comment warns about.
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
	}
}
