using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// What a HAZARD region does to a player who walks into it (#504) - the behaviour half of a scripted hazard area.
	// The lava on Ymir's Pass is the decoded case; the shape generalises to every other hazard family the client
	// carries.
	//
	// A 1:1 SIDE TABLE on MapRegion (#679), keyed by the region's own id - the NpcTemplate/NpcStatCore pattern. It
	// used to be a table of its own carrying a full copy of the geometry (a `<boundedarea>` prism plus a parallel
	// `MapHazardVertex` ring), which was the same physical thing stored twice: the same RDB record, the same derived
	// key, and two near-identical `EnvironmentService.Contains` overloads over one shared `InPrism`.
	//
	// The split is not tidiness, it is the subject of the table. The GEOMETRY is CLIENT data - the client's RDB type
	// 1000053 (BOUNDEDAREAS) ships 8,323 named `<boundedarea>` prisms and the lava volumes are in there by name
	// (`BA_Lava1..22` on Ymir's Pass, `BA_lava_dmg1..4` in the Amphitheatre of Karutonia, `LavaDamage-1..7` in Tortage
	// Volcano) because the client needs them to draw the lava and to run its own
	// `BoundedAreaContainer_c::Enter starting script`. The BEHAVIOUR is not: the lava volumes all carry
	// `<scryid>0</scryid>`, i.e. no client script, and the server's own feedback names its script class:
	//
	//   <remoteformat token="showfifomessagefromserver(14)|playerenter(3)|GenericHazardiousBoundedArea" category="50018" ...>
	//
	// `GenericHazardiousBoundedArea` with a `playerenter(3)` trigger. So the client owns WHERE and the server owns
	// WHAT - and as a side table that split is STRUCTURAL rather than a comment on a column someone will eventually
	// violate. It is also what lets a region's geometry be imported without implying the region is a hazard, which is
	// exactly what used to block importing the other hazard families even as inert volumes.
	//
	// ⚠️ So the earlier plan - reconstruct a hull from the positions retail was seen firing at, the way MapArea had to
	// for area banners - was the wrong instinct HERE, and it is worth saying why the two differ. Do not port a
	// sampled-hull + slack approach onto this geometry; it would replace 103 exact polygons with 1 point.
	//
	// <b>The geometry is validated against the wire, not just assumed.</b> In `chat-annotated-4` the lava kills the
	// player on Ymir's Pass (map 1090, pinned from the MapChange at 20:24:35). Walking the position track against the
	// polygons, the player's FIRST sample inside `BA_Lava7` is at 20:25:58.444 (1289.3, 238.3, 473.5) and the server's
	// `GenericHazardiousBoundedArea` feedback arrives at 20:25:58.706 - <b>262 ms later</b> - with the crossing Y inside
	// that volume's 235.0..241.7 range, and every later sample still inside. The preceding sample (0.4 s earlier) is
	// 0.30 units OUTSIDE the edge. So the client's polygon is the volume retail triggers on, to within one ~200 ms
	// position sample, and the runtime test needs NO tolerance - see EnvironmentService.Contains.
	//
	// ⚠️ Only the LAVA family gets a row, and that is a deliberate limit rather than an oversight. The same 1000053
	// records hold hundreds of other hazard-shaped names (`TrapBA`, `BA_SpikeTrap`, `SnakePitBA`, `PlayerDrowns_Dies`,
	// `BA_PoisonFog`, `BA_WaterDamage-`, `BADeathBridge`, ...) whose behaviour NOTHING in the corpus shows. Giving them
	// rows would mean shipping hundreds of guessed kill-volumes, and a mis-guessed one silently kills players on a map
	// nobody is testing - strictly worse than not having them. The families are enumerated on the Gitea follow-up so
	// adding one is a small, evidenced change.
	[PrimaryKey(nameof(RegionId))]
	public class MapHazard
	{
		// The region this behaviour is attached to - the identity, and where the name, the playfield and the whole
		// prism live. Cascades with it: geometry without behaviour is a plain region, behaviour without geometry is
		// nothing at all.
		public uint RegionId { get; set; }

		[ForeignKey(nameof(RegionId))]
		public virtual MapRegion Region { get; set; } = null!;

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
	}
}
