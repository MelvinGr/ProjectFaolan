using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One destination a local travel NPC offers: a wagoneer, ship captain or caravan master who moves the player
	// for a fee. Distinct from the zone-line doors the `world-teleports` module handles - those are a captured
	// `UseObject` -> `N3Teleport` on a fixed door - while this is a per-NPC list the player chooses from inside an
	// ordinary Knubot conversation, which is why it needs a table.
	//
	// The row *is* the conversation wiring. It is keyed by the dialogue edge the player's pick takes -
	// `(FromNodeId, OptionId)` - so the runtime needs no separate "this answer sells a trip" overlay: taking a branch
	// that has a row here is the trip. The screen that presents the destination list, which is where retail charges the
	// fare, is the edge's own From screen - stated by the key rather than derived by string surgery on an answer path.
	//
	// It is keyed by the branch and not by destination map, and that is not a style choice: Wagoner Cordelia offers
	// nine destinations and every one of them is on her own map (Field of the Dead). A `(NpcName, DestinationMapId)`
	// key could hold exactly one of them.
	//
	// The flow, decoded end to end from NecroL80_..._chat-annotated-4 (two complete trips, Wagoner Cordelia at t=767..782
	// and Wagoner Treva at t=1023..1030):
	//
	//   1. talk to the NPC -> greeting + an answer list offering "I am willing to pay for a ride on your wagon
	//      <font color="red">(Pay to travel)</font>";
	//   2. the player picks it -> SetDialogueCamera, "I'll take you wherever you like, once you've paid.", and a
	//      KnubotStartTradeIIR - retail takes the fare through the Knubot trade window, not through the pick;
	//   3. the client answers KnubotFinishTradeIIR, and the server sends DispatchUpdateCash (the new balance) followed
	//      by "Your coin is good enough. Where to?" + the destination answer list. The fee is therefore charged at the
	//      screen that presents the list, one step before any destination is chosen;
	//   4. the player picks a destination -> StopIIR, KnubotCloseChatWindow, a GameActionListIIR carrying
	//      "xpos=1136;ypos=133;zpos=223;PlayFieldID=1020;Local=1", the "Racer" buff, IntendedTeleport + N3Teleport, and
	//      the player is re-streamed at exactly that position.
	//
	// There is no confirmation box anywhere in that flow. The `DispatchScryShowMessageBox` "Do you want to travel to
	// Ymir's Pass?" in the same capture belongs to the zone-line door the player walked into ~28 s after Cordelia
	// dropped them at the border (they select door 0x863 at t=809.8, the box follows, and answering it does the
	// cross-map redirect). It is not travel's last step.
	//
	// A destination is usually the same map. Both captured trips are `Local=1` - a relocate in place with no loading
	// screen - because a wagoneer moves you within the region to the entrance of whatever you picked. Cross-map
	// travellers exist (the Tortage Smuggler and Wagoner Polus both carry an inline `{teleportplayer}` directive in
	// their dialogue text instead), so the runtime still tells the two apart by comparing DestinationMapId with the
	// player's current map and picks the right wire path.
	[PrimaryKey(nameof(FromNodeId), nameof(OptionId))]
	public class TravelDestination
	{
		// The screen that presents the destination list - "Your coin is good enough. Where to?" - and is therefore
		// where the fare comes off, one pick before any destination is chosen.
		public uint FromNodeId { get; set; }

		[ForeignKey(nameof(FromNodeId))]
		public virtual ConversationNode FromNode { get; set; } = null!;

		// The destination option's own LDB string id - which entry of that list buys this trip.
		public uint OptionId { get; set; }

		// The travel NPC's display name. Not part of the key (the screen already belongs to exactly one NPC), and not
		// what anything looks up by either: the runtime reaches a row through the branch that is its key
		// (`FromNodeId`, or `(FromNodeId, OptionId)` for the pick), so the name is purely the label the refusal text,
		// the log lines and `.travel` read. The GM command is the one place a name still resolves a row, because a GM
		// types one.
		[MaxLength(128)]
		public string NpcName { get; set; } = null!;

		public uint DestinationMapId { get; set; }

		[ForeignKey(nameof(DestinationMapId))]
		public virtual Map DestinationMap { get; set; } = null!;

		// The fare in the wallet's own units, measured as the wallet delta at step 3: 50,000 for Cordelia (level band
		// "L40 to 49") and 70,000 for Treva ("L50 to 59"). It is a per-NPC price - every destination the same wagoneer
		// offers costs the same, because retail charges once, before the list is even shown - so every row of one NPC
		// repeats it and the runtime charges it once per visit to the list screen.
		//
		// Null = the capture did not state it, which is not the same as free. Wagoner Glendon's trip is the case:
		// its DispatchUpdateCash is the first wallet update in that capture, so there is no earlier balance to
		// subtract and the fare is unmeasured. A 0 here would have shipped "Glendon gives free rides" as a fact.
		public uint? Cost { get; set; }

		// Where the player lands, straight out of the trip's GameActionListIIR (`xpos`/`ypos`/`zpos`; ypos is the
		// height). Null = use the destination map's default RespawnPoint, for a destination whose coordinates have not
		// been captured.
		public float? ArrivalX { get; set; }
		public float? ArrivalY { get; set; }
		public float? ArrivalZ { get; set; }

		// How long the NPC's parting line stays on screen before the trip runs. 0 on every captured wagoner
		// trip - retail teleports on the pick, with no pause - and non-zero only on the `{teleportplayer}` travellers,
		// whose dialogue text states it inline: `{delay 6}` for the Tortage Smuggler, `{delay 3}` for Wagoner Polus,
		// and none at all for Sigurd. Measured against the capture: the Smuggler's line lands at t=105.8 and the
		// IntendedTeleport at t=111.7.
		public float DelaySeconds { get; set; }

		// This row was harvested from an inline `{teleportplayer}` directive in the destination screen's own
		// dialogue text, rather than from a captured trip's GameActionListIIR. Both produce the same trip and take the
		// same runtime path; the flag exists because the two have different evidence and different completeness - a
		// directive row's arrival is exactly what the text says, and it never states a fare (there is none) or a
		// destination list, so a null `Cost` here means "free", not "unmeasured".
		public bool FromDialogueDirective { get; set; }
	}
}
