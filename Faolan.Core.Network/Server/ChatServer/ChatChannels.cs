using System.Collections.Generic;

namespace Faolan.Core.Network.Server.ChatServer
{
	/// <summary>
	///     One chat channel's IDENTITY minus its id: the (type, name, flag) triple every
	///     <see cref="Codecs.Text.ChannelSubscription" /> for it carries. Split from the id because the two halves have
	///     different lifetimes - the triple is fixed per channel, while the id is either a constant (the standing
	///     channels) or the thing the player currently belongs to (their map, guild or team).
	/// </summary>
	public sealed record ChatChannel(byte Type, string Name, uint Flag);

	/// <summary>
	///     Every chat channel the server offers, declared in ONE place.
	///     <para>
	///         There are two kinds and the difference is the id, not the mechanism: a <see cref="Standing" /> channel's
	///         id is a constant, and a <see cref="Playfield" /> / <see cref="Guild" /> / <see cref="Team" /> channel's
	///         id is what the player currently belongs to - so those three MOVE, and a move is drop-then-subscribe on
	///         the same descriptor.
	///     </para>
	///     <para>
	///         ⚠️ Subscription ORDER is not significant, which is what lets this be a list at all. Retail varies its
	///         own: the Hoxxling login sends the list three times in one burst as <c>Trial,Global,LFG,Playfield,
	///         NewbieHelp</c>, then <c>Global,LFG,Trial,Playfield,NewbieHelp</c>, then the full set with
	///         GlobalEnglish - three different orders, same session.
	///         [V2: Hoxxling_mage-heraldofxotli_login.tshark.json frame 114]
	///     </para>
	/// </summary>
	public static class ChatChannels
	{
		// The standing channels: always subscribed, ids fixed per channel. ~Trial is the one that does not carry the
		// common 0x00008044 flag. [V2: Hoxxling_mage-heraldofxotli_login.tshark.json frame 114]
		public static readonly ChatChannel Trial = new ChatChannel(0x04, "~Trial", 0x00008244);
		public static readonly ChatChannel Global = new ChatChannel(0x04, "~Global", 0x00008044);
		public static readonly ChatChannel Lfg = new ChatChannel(0x04, "~LFG", 0x00008044);
		public static readonly ChatChannel GlobalEnglish = new ChatChannel(0x11, "~GlobalEnglish", 0x00008044);
		public static readonly ChatChannel NewbieHelp = new ChatChannel(0x87, "~NewbieHelp", 0x00008044);

		// The three that FOLLOW something the player belongs to. Their flag is the other one - 0x00008004 for the two
		// membership channels; ~Playfield keeps the standing flag even though its id moves, so "moving" and "which
		// flag" are genuinely separate facts and neither can be derived from the other.
		//
		// ~Playfield's id is the map (#562); ~Guild's is the guild id, the same word the create/full-update header
		// carries (#663); ~Team's is the team id, the one DispatchYouJoinedTeam names as an ID32 of type 0xDEA9 (#668).
		// [V2: DemoL80_mage-demonologist_login_create-guild.tshark.json frame 369]
		// [V2: login_2-clients_group-trade-friend.tshark.json frame 701]
		public static readonly ChatChannel Playfield = new ChatChannel(0x90, "~Playfield", 0x00008044);
		public static readonly ChatChannel Guild = new ChatChannel(0x03, "~Guild", 0x00008004);
		public static readonly ChatChannel Team = new ChatChannel(0x82, "~Team", 0x00008004);

		// The server-defined assistant channel (#100) - NOT a retail channel: posts on it are bridged out through the
		// MCP chat tools, and it is offered only while that host is running, because without it nothing reads the log
		// or can answer. It is declared here with the rest rather than as its own constants precisely because it is
		// the odd one out: the shape is identical to a retail channel's, so the only thing that should distinguish it
		// is this comment and the gate at the call site.
		//
		// Retail "~"-prefixed names are client-localized, so a plain name displays verbatim. The id sits far outside
		// the retail range (0x2328..0x2335, ~NewbieHelp 0x1E8480), so it can never collide with a real channel.
		public static readonly ChatChannel Assistant = new ChatChannel(0x04, ClaudeChannel.ChannelName, 0x00008044);

		/// <summary>
		///     The standing channels with their fixed ids, in the order the login burst sends them.
		///     <paramref name="includeAssistant" /> is the MCP host's gate (see <see cref="Assistant" />).
		/// </summary>
		public static IEnumerable<(ChatChannel Channel, uint Id)> StandingAtLogin(bool includeAssistant)
		{
			yield return (Trial, 0x0000232A);
			yield return (Global, 0x00002328);
			yield return (Lfg, 0x00002329);
			yield return (GlobalEnglish, 0x00002335);
			yield return (NewbieHelp, 0x001E8480);

			if (includeAssistant)
				yield return (Assistant, ClaudeChannel.ChannelId);
		}
	}
}
