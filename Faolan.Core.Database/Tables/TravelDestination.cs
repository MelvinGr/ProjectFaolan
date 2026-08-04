using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One destination a LOCAL TRAVEL NPC offers (#506): a wagoneer, ship captain or caravan master who moves the player
	// for a fee. Distinct from the zone-line doors the `world-teleports` module handles - those are a captured
	// `UseObject` -> `N3Teleport` on a fixed door - while this is a per-NPC LIST the player chooses from inside an
	// ordinary Knubot conversation, which is why it needs a table.
	//
	// ⚠️ THE ROW *IS* THE CONVERSATION WIRING (#514). It is keyed by the dialogue node the player's pick lands on -
	// `(NpcName, AnswerPath)` - so the runtime needs no separate "this answer sells a trip" overlay: serving a screen
	// whose (npc, path) has a row here IS the trip. The NPC is identified by NAME, never by a spawn handle, for the
	// same reason conversations and shops are; the answer path is the stable branch key the dialogue tree already uses
	// (ConversationNode.AnswerPath). TypeId is not part of the key - every captured travel tree is the ordinary talk
	// type 42008, so it would only ever repeat.
	//
	// ⚠️ It is keyed by ANSWER PATH and not by destination map, and that is not a style choice: Wagoner Cordelia offers
	// NINE destinations and every one of them is on her own map (Field of the Dead). A `(NpcName, DestinationMapId)`
	// key - what this table shipped with until #514 - can hold exactly one of them.
	//
	// The flow, decoded end to end from NecroL80_..._chat-annotated-4 (two complete trips, Wagoner Cordelia at t=767..782
	// and Wagoner Treva at t=1023..1030):
	//
	//   1. talk to the NPC -> greeting + an answer list offering "I am willing to pay for a ride on your wagon
	//      <font color="red">(Pay to travel)</font>";
	//   2. the player picks it -> SetDialogueCamera, "I'll take you wherever you like, once you've paid.", and a
	//      KnubotStartTradeIIR - retail takes the FARE through the Knubot TRADE window, not through the pick;
	//   3. the client answers KnubotFinishTradeIIR, and the server sends DispatchUpdateCash (the new balance) followed
	//      by "Your coin is good enough. Where to?" + the DESTINATION answer list. The fee is therefore charged at the
	//      screen that PRESENTS the list, one step before any destination is chosen;
	//   4. the player picks a destination -> StopIIR, KnubotCloseChatWindow, a GameActionListIIR carrying
	//      "xpos=1136;ypos=133;zpos=223;PlayFieldID=1020;Local=1", the "Racer" buff, IntendedTeleport + N3Teleport, and
	//      the player is re-streamed at exactly that position.
	//
	// ⚠️ There is NO confirmation box anywhere in that flow. The `DispatchScryShowMessageBox` "Do you want to travel to
	// Ymir's Pass?" in the same capture belongs to the ZONE-LINE DOOR the player walked into ~28 s after Cordelia
	// dropped them at the border (they select door 0x863 at t=809.8, the box follows, and answering it does the
	// cross-map redirect). Reading it as travel's last step is what made the pre-#514 model wrong.
	//
	// ⚠️ A destination is USUALLY the same map. Both captured trips are `Local=1` - a relocate in place with no loading
	// screen - because a wagoneer moves you within the region to the entrance of whatever you picked. Cross-map
	// travellers exist (the Tortage Smuggler and Wagoner Polus both carry an inline `{teleportplayer}` directive in
	// their dialogue text instead), so the runtime still tells the two apart by comparing DestinationMapId with the
	// player's current map and picks the right wire path.
	[PrimaryKey(nameof(NpcName), nameof(AnswerPath))]
	public class TravelDestination
	{
		// The travel NPC's display name - the stable key (see above).
		[MaxLength(128)]
		public string NpcName { get; set; } = null!;

		// The dialogue branch this destination sits on: the ConversationNode.AnswerPath of the screen the pick lands
		// on ("0>3" = the player picked option 0 at the greeting, then option 3 from the destination list). The
		// PARENT of this path is the screen that presents the list, which is where the fare is charged - derived
		// rather than stored, because retail's own tree already states it.
		[MaxLength(128)]
		public string AnswerPath { get; set; } = null!;

		public uint DestinationMapId { get; set; }

		[ForeignKey(nameof(DestinationMapId))]
		public virtual Map DestinationMap { get; set; } = null!;

		// The fare in the wallet's own units, measured as the wallet delta at step 3: 50,000 for Cordelia (level band
		// "L40 to 49") and 70,000 for Treva ("L50 to 59"). It is a per-NPC price - every destination the same wagoneer
		// offers costs the same, because retail charges once, before the list is even shown - so every row of one NPC
		// repeats it and the runtime charges it once per visit to the list screen.
		//
		// ⚠️ NULL = the capture did not state it, which is NOT the same as free. Wagoner Glendon's trip is the case:
		// its DispatchUpdateCash is the FIRST wallet update in that capture, so there is no earlier balance to
		// subtract and the fare is unmeasured. A 0 here would have shipped "Glendon gives free rides" as a fact.
		public uint? Cost { get; set; }

		// Where the player lands, straight out of the trip's GameActionListIIR (`xpos`/`ypos`/`zpos`; ypos is the
		// height). Null = use the destination map's default RespawnPoint, for a destination whose coordinates have not
		// been captured.
		public float? ArrivalX { get; set; }
		public float? ArrivalY { get; set; }
		public float? ArrivalZ { get; set; }
	}
}
