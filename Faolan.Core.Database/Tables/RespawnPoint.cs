using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A resurrection point on a map - where a dead player can revive. Players must DISCOVER a point (walk near it)
	// before they can respawn there; the starter point on each map is always known (AlwaysKnown). On death a player
	// respawns at the CLOSEST point they know. Position/rotation come from Base3DEntity.
	public class RespawnPoint : Base3DEntity
	{
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		// True for the always-available starter point (e.g. Tortage Beach) - known without discovery.
		public bool AlwaysKnown { get; set; }

		// The OUTLAW variant of a point: where a criminal-flagged ("shady") character revives instead of the normal one.
		// Straight from the RDB's own `shady` attribute (type 1000090) - 53 of 726 exported points carry shady="true",
		// and the flag and the designers' labels agree exactly: every one of them is named for the outlaw system
		// ("Outlaw Camp" x12, "EntranceShady0" x12, "Resurrection_OutlawCamp", "Respoint_ShadyCamp", "ShadyResAIPoint0",
		// "Conarch Village - Shady"), and NO shady="false" point has an outlaw/shady name. 222 older-dialect elements
		// omit the attribute; absent reads as false, which is safe for the same reason - none of them is outlaw-named.
		//
		// Imported but NOT yet consumed: the revive path still picks the closest KNOWN point regardless of criminal
		// state, so an outlaw currently revives at the normal point. Wiring it up is Gitea #517 (the criminal state
		// itself is #503).
		public bool Shady { get; set; }

		// The client's DISCOVERY volume for this point (#519): walk into it and the point becomes known. Null for
		// the 525 points the client ships no volume for, which fall back to WorldSimulation's proximity radius.
		//
		// The volume is not a courtesy - it is measurably tighter than the radius it replaces. The 13 shipped ones
		// are 16x20 to 29x33 units and only ~10 units tall, against a 30-unit radius (a 60x60 box) with no vertical
		// bound at all, so we currently discover a point from roughly twice the distance retail requires and from
		// any height. SetNull rather than cascade: losing the volume must degrade to the radius, never delete the
		// respawn point.
		public uint? DiscoveryRegionId { get; set; }

		[ForeignKey(nameof(DiscoveryRegionId))]
		public virtual MapRegion? DiscoveryRegion { get; set; }

		// The RDB's `RestorationLevel`: the PERCENT of max health/mana you revive with here (observed 7/10/20/25/30/
		// 50/70/100; 344 of the 538 points restore 10, 157 restore 100). Applied by the revive path via
		// EntityVitals.ReviveTo (#375), and it GOES ON THE WIRE - it is the field the resurrection-spots body used to
		// carry as an undecoded "0x0A constant" (ResurrectionSpotsBody). For all 21 spot ids that appear in the
		// captures the wire value equals the value imported here, so this column is retail-verified, not inferred.
		public float RestorationLevel { get; set; }

		// The RDB's world-editor object label ("ResurrectionAIPoint0", occasionally a designer name like "Outlaw
		// Camp"). NOT a display name and NOT unique - 95 points share "ResurrectionAIPoint0". Nothing player-facing
		// should ever render it. The real name lives in the LDB (page 211698 / category 270: "Water's Edge",
		// "Secluded Cove", ...), but the server never needs it: the CLIENT names the point in its own discovery and
		// release-screen UI, and the wire only carries the id (#372). Treat this as a dev label.
		[MaxLength(128)]
		public string Name { get; set; } = null!;
	}
}
