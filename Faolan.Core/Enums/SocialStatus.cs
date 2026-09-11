namespace Faolan.Core.Enums
{
	/// <summary>
	///     A player's presence as the social tier sees it - what a friend-list row shows next to their name.
	/// </summary>
	/// <remarks>
	///     One enum used in two directions: the client sets its own with chat c2s <c>SetSocialStatus</c> (0x002A), and
	///     every watcher is told one in the <c>online</c> byte of a friend's state record (FriendStateCodec).
	///     <para />
	///     The values are the client's own, recovered from three places that agree:
	///     <list type="number">
	///         <item>
	///             The friend-list icon table: six literals laid out consecutively in the friends-list view, in this
	///             order - <c>friendslist/icon_offline.tga</c>, <c>icon_online.tga</c>, <c>icon_lfg.tga</c>,
	///             <c>icon_away.tga</c>, <c>icon_other.tga</c>, <c>icon_ignored.tga</c> (FUN_016bc7f0 @0x016bc7f0
	///             builds all six).
	///         </item>
	///         <item>
	///             The client's own switch: <c>FriendListNode_c::PresenceCategory</c> (FUN_006ca510) maps 1 to
	///             category 0, 2 to category 1, 3 to category 2, and everything else to the offline category - so 1, 2
	///             and 3 are all online states, in the icon table's order, and the social-list row builder counts
	///             exactly those three towards its online tally.
	///         </item>
	///         <item>
	///             The wire: in NecroL80_..._annotated_9 the client toggles LFG on and off three times, and each toggle
	///             emits <c>SetLookingForTeamStatus</c> on the CS tier and <c>SetSocialStatus</c> on the chat tier
	///             ~50 microseconds apart - flag on sends 2, flag off sends 1. One UI action, two tiers.
	///         </item>
	///     </list>
	///     <see cref="Online" /> and <see cref="LookingForGroup" /> are observed on the wire; <see cref="Offline" />
	///     is what the client renders for anything outside 1..3, and what we send for a friend with no live
	///     connection.
	/// </remarks>
	public enum SocialStatus : byte
	{
		/// <summary>
		///     Not connected. The client never sends this about itself - it disconnects instead - so this is the value
		///     the server puts in a friend record for someone who is not here.
		/// </summary>
		Offline = 0,
		Online = 1,
		LookingForGroup = 2,

		/// <summary>
		///     The third online category. The icon table names it and the client's switch routes it, but no capture
		///     carries it: the AFK/away toggle was never used in a traced session. Modelled because leaving a hole at
		///     3 would make the two values above look like a two-state flag. [V0]
		/// </summary>
		Away = 3
	}
}
