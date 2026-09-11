using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.CsPlayerAgent
{
	/// <summary>
	///     What the CsPlayerAgent listener exposes to the other listeners - chiefly the team roster, which it owns.
	///     <para>
	///         A team spans two tiers: the CS tier owns the roster RPCs (invite / accept / decline and every roster
	///         dispatch), while <c>LeaveTeam</c> and <c>KickFromTeam</c> arrive on the player's GAME connection. So the
	///         world listener has to reach the roster, and does so through this seam rather than holding the service -
	///         the same shape every other cross-listener action uses (<see cref="GameServer.IGameServerDispatcher" /> for
	///         world actions, <see cref="ChatServer.IChatServerDispatcher" /> for chat), and for the same reason: a
	///         listener only exists once the host starts it, and the CS-to-world relationship is mutual, since the roster
	///         pushes group-membership IIRs back onto world connections.
	///     </para>
	/// </summary>
	public interface ICsPlayerAgentProvider
	{
		/// <summary>
		///     A character leaves its team (c2s LeaveTeam on the game tier, or a logout). Dissolves a team left with one
		///     member and promotes a new leader when the leader was the one to go. Returns false when the character was
		///     not in a team.
		/// </summary>
		Task<bool> LeaveTeam(uint characterId);

		/// <summary>
		///     A team leader removes <paramref name="targetId" /> (c2s KickFromTeam on the game tier). Returns false when
		///     the caller is not the team's leader, so the caller can surface the refusal rather than assume it worked.
		/// </summary>
		Task<bool> KickFromTeam(uint leaderId, uint targetId);

		/// <summary>
		///     Every member of <paramref name="characterId" />'s team, including that character, or empty when solo. The
		///     read half of the same ownership split: a quest share arrives on the game connection and has to know who
		///     the sharer's team-mates are, and the roster lives here.
		/// </summary>
		IReadOnlyList<uint> TeamMembersOf(uint characterId);

		/// <summary>
		///     The id of the team <paramref name="characterId" /> belongs to, or null when solo. The team's identity
		///     rather than its roster, because that is what a team-owned playfield instance is keyed by: the entry's
		///     <c>PlayfieldProxy</c> carries <c>(0xDEA9, teamId)</c> in its owner slots, so the world listener needs the
		///     id itself and not the members.
		/// </summary>
		uint? TeamIdOf(uint characterId);

		/// <summary>
		///     The started custom PvP match holding <paramref name="characterId" /> whose minigame map is
		///     <paramref name="mapId" />, or null. The world entry asks this the same way it asks
		///     <see cref="TeamIdOf" />: the match's id keys the match-owned playfield instance, so both sides land in
		///     one copy, and the seconds tell the entering client the match clock
		///     (<c>DispatchMinigameMatchStarted</c>).
		/// </summary>
		PvpMatchWorldEntry? PvpMatchEntryOf(uint characterId, uint mapId);

		/// <summary>
		///     Whether <paramref name="attackerId" /> may damage <paramref name="victimId" /> as PvP opponents: true only
		///     when both hold membership of the same started, unfinished custom PvP match on different sides.
		///     <para>
		///         This is the whole PvP gate, and it lives here because the match does. Retail also allows PvP by
		///         playfield - the client ships that table (RDB 1000509/1000510/1000512, decoded in
		///         docs/rdb/client-data-tables.md) - but the ruleset columns are not named, so nothing here reads them
		///         and open-world PvP stays refused. A minigame needs no such table: the sides are locked and the fight
		///         is the point. Open work tracked in Gitea #503.
		///     </para>
		/// </summary>
		bool ArePvpMatchOpponents(uint attackerId, uint victimId);

		/// <summary>
		///     Scores a PvP-match kill: a point for the killer, a death for the victim, and - when a side reaches the
		///     ruleset's score limit - the match ends there rather than at its time limit.
		/// </summary>
		Task CreditPvpMatchKill(uint killerId, uint victimId);

		/// <summary>
		///     The GM <c>.matchstart</c> override: forms a PvP match from whoever is compatibly queued right now, however
		///     few - down to one member, for a solo walk of the teleport and instance flow. Returns a one-line outcome
		///     for the GM's chat. The command arrives on the game tier; the queue lives here.
		/// </summary>
		Task<string> ForcePvpMatchStart();

		/// <summary>
		///     The GM <c>.matchstop</c> override: ends <paramref name="characterId" />'s started match now, telling every
		///     member through the client's own DispatchForceStopMinigame path. Returns a one-line outcome.
		/// </summary>
		Task<string> ForcePvpMatchStop(uint characterId, string message);

		/// <summary>
		///     Tells <paramref name="recipientId" /> that mail has arrived
		///     (<c>DispatchYouGotMailNotification</c>). Returns false when the recipient is not connected to this realm's
		///     CS tier, so the caller can tell "not notified" from "notified".
		///     <para>
		///         The split is the message's own: mail is written on the game connection, where the mailbox
		///         transactions live, but the notification rides <c>CSPlayerInterface</c>, the router tier, which is how
		///         it reaches a player who is nowhere near a mailbox.
		///     </para>
		/// </summary>
		Task<bool> NotifyMailReceived(uint recipientId, ulong mailId);

		/// <summary>
		///     Credits <paramref name="amountTin" /> to the guild <paramref name="characterId" /> belongs to - the deposit
		///     half of the guild bank, and the mirror of the withdrawal's
		///     <c>IGameServerDispatcher.CreditCharacterCash</c>. Returns false when the character is in no guild, or the
		///     guild is not on this realm's CS tier, so the caller can refund the wallet it has already debited.
		///     <para>
		///         The split is the client's own: <c>GuildTransferMoney</c> arrives on the game connection because that
		///         is where the wallet lives, while <c>Guild.Cash</c> lives here. Whoever holds the money does the debit
		///         first and the other side is credited across this seam; see <c>GuildService.DepositToGuildBank</c> for
		///         why that order is the safe one for a money handler.
		///     </para>
		/// </summary>
		Task<bool> DepositToGuildBank(uint characterId, ulong amountTin);
	}

	/// <summary>A started match's identity as the world entry needs it: the instance key and the running clock.</summary>
	public readonly record struct PvpMatchWorldEntry(uint MatchId, uint SecondsSinceStarted);
}
