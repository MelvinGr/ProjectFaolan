namespace Faolan.Core.Enums
{
	// A player's presence as the SOCIAL tier sees it - what a friend's list row shows next to their name. It is one
	// enum used in two directions (#892): the client SETS its own with chat c2s `SetSocialStatus` (0x002A), and every
	// watcher is TOLD one in the `online` byte of a friend's state record (FriendStateCodec).
	//
	// The values are the client's own, recovered from three places that agree:
	//
	//  1. **The friend-list icon table.** Six literals laid out consecutively in the friends-list view, in this order:
	//     `friendslist/icon_offline.tga`, `icon_online.tga`, `icon_lfg.tga`, `icon_away.tga`, `icon_other.tga`,
	//     `icon_ignored.tga` (`FUN_016bc7f0` @0x016bc7f0 builds all six).
	//  2. **The client's own switch.** `FriendListNode_c::PresenceCategory` (`FUN_006ca510`) maps 1 -> category 0,
	//     2 -> category 1, 3 -> category 2, and everything else to the offline category - so 1, 2 and 3 are all
	//     ONLINE states, in the icon table's order, and the social-list row builder counts exactly those three
	//     towards its online tally.
	//  3. **The wire.** In `NecroL80_..._annotated_9` the client toggles LFG on and off three times, and each toggle
	//     emits `SetLookingForTeamStatus` on the CS tier and `SetSocialStatus` on the chat tier ~50 MICROSECONDS
	//     apart: flag ON sends 2, flag OFF sends 1. One UI action, two tiers.
	//
	// So `Online` and `LookingForGroup` are OBSERVED on the wire; `Offline` is what the client renders for anything
	// outside 1..3 (and what we send for a friend with no live connection, as every captured record does).
	public enum SocialStatus : byte
	{
		// Not connected. The client never sends this about itself - it disconnects instead - so this is the value the
		// SERVER puts in a friend record for someone who is not here.
		Offline = 0,
		Online = 1,
		LookingForGroup = 2,

		// The third online category. The icon table names it and the client's switch routes it, but no capture carries
		// it: the AFK/away toggle was never used in a traced session. Modelled because leaving a hole at 3 would make
		// the two values above look like a two-state flag, which is exactly the reading this enum replaces. [V0]
		Away = 3
	}
}
