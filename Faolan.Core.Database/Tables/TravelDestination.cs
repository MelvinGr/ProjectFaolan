using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One destination a LOCAL TRAVEL NPC offers (#506): a wagoneer, ship captain or caravan master who moves the player
	// to another playfield for a fee. Distinct from the zone-line doors the `world-teleports` module already handles -
	// those are a captured `UseObject` -> `N3Teleport` on a fixed door, while this is a per-NPC LIST the player chooses
	// from, which is why it needs a table.
	//
	// Keyed by (NpcName, DestinationMapId): a travel NPC is identified by NAME, never by a spawn handle, for the same
	// reason conversations and shops are - the handle is session-volatile and the name is the stable key.
	//
	// The flow the captures show, end to end (NecroL80_..._chat-annotated-4):
	//
	//   1. an ordinary Knubot conversation with the NPC, where the player picks a destination;
	//   2. the fee is charged AT THE PICK - the wallet drops by a round 50,000 at 20:16:41, ~40 s before anything else
	//      happens, which is what `Cost` records;
	//   3. a scry confirmation box naming the destination map (ScryMessageBoxCodec);
	//   4. on confirm, a redirect + MapChange to that map.
	//
	// ⚠️ The player arrives at the destination's ENTRANCE, not at an arbitrary spot - so a destination needs an arrival
	// POINT, not just a map id. `ArrivalX/Y/Z` carries it, and when it is null the runtime falls back to the
	// destination map's default RespawnPoint, which is the closest thing to "the entrance" we already have per map.
	//
	// ⚠️ A destination is NOT always another playfield. Melvin's own description of the mechanic: local travel moves
	// you within the map/region to the entrance of whatever you picked, and there are SEPARATE travellers that cross
	// maps. Both are this one row shape - a same-map destination simply sets DestinationMapId to the map the NPC
	// stands on and relies on ArrivalX/Y/Z - and the runtime tells them apart to pick the right wire path: a same-map
	// trip relocates in place (no loading screen), a cross-map trip goes through the full changemap handoff. That is
	// why ArrivalX/Y/Z are here rather than being derived from the destination map alone: for a same-map destination
	// the map id carries no information at all.
	[PrimaryKey(nameof(NpcName), nameof(DestinationMapId))]
	public class TravelDestination
	{
		// The travel NPC's display name - the stable key (see above).
		public string NpcName { get; set; } = null!;

		public uint DestinationMapId { get; set; }

		[ForeignKey(nameof(DestinationMapId))]
		public virtual Map DestinationMap { get; set; } = null!;

		// The fee in the wallet's own units. 50,000 is the one directly observed price (Ymir's Pass); a 0 means free,
		// which the corpus also shows - the Amphitheatre of Karutonia trip moves no money at all.
		public uint Cost { get; set; }

		// Where the player lands. Null = use the destination map's default RespawnPoint (see the note above).
		public float? ArrivalX { get; set; }
		public float? ArrivalY { get; set; }
		public float? ArrivalZ { get; set; }
	}
}
