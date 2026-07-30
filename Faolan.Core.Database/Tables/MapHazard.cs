using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A scripted HAZARD AREA on a playfield: a region that hurts or kills a player who walks into it (#504). The lava
	// on Ymir's Pass is the decoded case; the shape generalises to every other hazard family the client carries.
	//
	// The geometry is CLIENT DATA, and finding that is what made this a real feature rather than a one-point guess.
	// The client's RDB type 1000053 (BOUNDEDAREAS, 478 records) is plaintext XML, one record PER PLAYFIELD (the record
	// id IS the map id), holding 8323 named `<boundedarea>` prisms - each a `<vertice>` list of (x, z) pairs plus a
	// `<heights>` pair giving its Y range. The lava volumes are in there by name: `BA_Lava1..22` on Ymir's Pass,
	// `BA_lava_dmg1..4` in the Amphitheatre of Karutonia, `LavaDamage-1..7` in Tortage Volcano, and so on.
	//
	// ⚠️ So the earlier plan - reconstruct a hull from the positions retail was seen firing at, the way MapArea has to
	// for area banners - was the wrong instinct HERE, and it is worth saying why the two differ. A banner's region is
	// genuinely server-side (#348 scanned all 410,433 local RDB records for a banner's text id and found only chance
	// collisions). A hazard's region is not: the client needs it to draw the lava and to run its own
	// `BoundedAreaContainer_c::Enter starting script`, so it ships. Do not port MapArea's sampled-hull + slack
	// approach onto this table; it would replace 103 exact polygons with 1 point.
	//
	// <b>The geometry is validated against the wire, not just assumed.</b> In `chat-annotated-4` the lava kills the
	// player on Ymir's Pass (map 1090, pinned from the MapChange at 20:24:35). Walking the position track against the
	// polygons, the player's FIRST sample inside `BA_Lava7` is at 20:25:58.444 (1289.3, 238.3, 473.5) and the server's
	// `GenericHazardiousBoundedArea` feedback arrives at 20:25:58.706 - <b>262 ms later</b> - with the crossing Y inside
	// that volume's 235.0..241.7 range, and every later sample still inside. The preceding sample (0.4 s earlier) is
	// 0.30 units OUTSIDE the edge. So the client's polygon is the volume retail triggers on, to within one ~200 ms
	// position sample, and the runtime test needs NO tolerance - see EnvironmentService.Contains.
	//
	// What is NOT client data is the BEHAVIOUR - which effect, how much damage, whether it kills. The lava volumes all
	// carry `<scryid>0</scryid>`, i.e. no client script, and the server's own feedback names its script class:
	//
	//   <remoteformat token="showfifomessagefromserver(14)|playerenter(3)|GenericHazardiousBoundedArea" category="50018" ...>
	//
	// `GenericHazardiousBoundedArea` with a `playerenter(3)` trigger. So the split is: the client owns WHERE, the
	// server owns WHAT - which is why the effect columns below are ours to fill and the geometry is imported.
	//
	// ⚠️ Only the LAVA family is imported, and that is a deliberate limit rather than an oversight. The same 1000053
	// records hold hundreds of other hazard-shaped names (`TrapBA`, `BA_SpikeTrap`, `SnakePitBA`, `PlayerDrowns_Dies`,
	// `BA_PoisonFog`, `BA_WaterDamage-`, `BADeathBridge`, ...) whose behaviour NOTHING in the corpus shows. Importing
	// them would mean shipping hundreds of guessed kill-volumes, and a mis-guessed one silently kills players on a map
	// nobody is testing - strictly worse than not having them. The families are enumerated on the Gitea follow-up so
	// adding one is a small, evidenced change.
	//
	// A DERIVED key rather than the natural (MapId, Name) composite, for exactly NpcSpawnArea's reason: 5388 vertex
	// rows hang off these 103 hazards, and carrying a NAME string down into every one of them is the "identity too
	// wide to carry down a tree" case DerivedKey exists for. The id is a pure function of (MapId, Name) and the client
	// RDB is fixed content, so a re-import reproduces it exactly.
	[Index(nameof(MapId), nameof(Name), IsUnique = true)]
	public class MapHazard : BaseEntity, IDerivedKey
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

		// The client's own name for the volume (`BA_Lava7`, `LavaDamage-3`), verbatim from the RDB. Part of the
		// identity, and the only readable handle on a hazard when one misbehaves in-client.
		public string Name { get; set; } = null!;

		// The spell the hazard applies on entry, or 0 for none. Lava applies 3773837 "Incineration" (3 s) - the word
		// the player's own annotation used ("got incinerated by lava"). Distinct from the `Incinerate` /
		// `Incinerate (1..5)` family, which is the Herald of Xotli ability.
		//
		// The spell carries NO stat effects of its own (checked against Spells/SpellStatEffects): it is the visual and
		// the debuff icon. The damage is Damage/Lethal below, not the spell.
		public uint EffectSpellId { get; set; }

		// Health the hazard costs on entry; 0 when it kills outright instead (see Lethal).
		public uint Damage { get; set; }

		// Whether entering is fatal. Lava is: across the whole death window retail never sends the player a single
		// StatDamage, so this is not "damage >= health" - it is a distinct outcome with no damage record at all.
		public bool Lethal { get; set; }

		// The prism's Y range, from the RDB `<heights>` pair. Load-bearing rather than decoration: the lava flows on
		// Ymir's Pass sit at different altitudes (232..269 across the 22 volumes) and a bridge crosses some of them, so
		// testing (x, z) alone would kill a player walking safely overhead.
		public float MinY { get; set; }
		public float MaxY { get; set; }

		// The polygon's axis-aligned bounds, precomputed from the vertices. Purely a CHEAP REJECT so the runtime skips
		// the point-in-polygon walk for the 100+ volumes a move is nowhere near - not the containment test itself. The
		// polygons are concave ribbons tracing a lava flow (up to 583 vertices), so their AABBs overlap walkable ground
		// heavily and an AABB test would be badly wrong on its own.
		public float MaxX { get; set; }
		public float MaxZ { get; set; }
		public float MinX { get; set; }
		public float MinZ { get; set; }

		// The boundary polygon, in RDB order; cascade-delete with the hazard.
		public virtual ICollection<MapHazardVertex> Vertices { get; set; } = [];
	}
}
