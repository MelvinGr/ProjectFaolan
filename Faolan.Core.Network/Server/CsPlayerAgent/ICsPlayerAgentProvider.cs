using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.CsPlayerAgent
{
	/// <summary>
	///     What the CsPlayerAgent listener exposes to the OTHER listeners - currently the team roster, which it owns.
	///     <para>
	///         A team spans two tiers: the CS tier owns the roster RPCs (invite / accept / decline and every roster
	///         dispatch), while <c>LeaveTeam</c> and <c>KickFromTeam</c> arrive on the player's GAME connection. So the
	///         world listener has to reach the roster, and it does it through this seam rather than holding the service -
	///         the same shape every other cross-listener action uses (<see cref="GameServer.IGameServerDispatcher" /> for
	///         world actions, <see cref="ChatServer.IChatServerDispatcher" /> for chat), and for the same reason: a
	///         listener only exists once the host starts it, and the CS↔world relationship is mutual (the roster pushes
	///         group-membership IIRs back onto world connections).
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
		///     Every member of <paramref name="characterId" />'s team, INCLUDING that character, or empty when solo. The
		///     read half of the same ownership split: a quest share (#101) arrives on the game connection and has to know
		///     who the sharer's team-mates are, and the roster lives here.
		/// </summary>
		IReadOnlyList<uint> TeamMembersOf(uint characterId);

		/// <summary>
		///     The id of the team <paramref name="characterId" /> belongs to, or null when solo. The team's IDENTITY
		///     rather than its roster, because that is what a TEAM-OWNED playfield instance is keyed by (#512): the
		///     entry's <c>PlayfieldProxy</c> carries <c>(0xDEA9, teamId)</c> in its owner slots, so the world listener
		///     needs the id itself and not the members.
		/// </summary>
		uint? TeamIdOf(uint characterId);

		/// <summary>
		///     The STARTED custom PvP match holding <paramref name="characterId" /> whose minigame map is
		///     <paramref name="mapId" />, or null (#146). The world entry asks this the same way it asks
		///     <see cref="TeamIdOf" />: the match's id keys the match-owned playfield instance (both sides land in ONE
		///     copy - the minigame twin of #512's team instance), and the seconds tell the entering client the match
		///     clock (<c>DispatchMinigameMatchStarted</c>).
		/// </summary>
		PvpMatchWorldEntry? PvpMatchEntryOf(uint characterId, uint mapId);

		/// <summary>
		///     The GM `.matchstart` override (#146): form a PvP match from whoever is compatibly queued right now,
		///     however few (down to one member, for a solo walk of the teleport + instance flow). Returns a one-line
		///     outcome for the GM's chat. The command arrives on the game tier; the queue lives here.
		/// </summary>
		Task<string> ForcePvpMatchStart();

		/// <summary>
		///     Tell <paramref name="recipientId" /> that mail has arrived (<c>DispatchYouGotMailNotification</c>, #871).
		///     Returns false when the recipient is not connected to this realm's CS tier, so the caller can tell "not
		///     notified" from "notified".
		///     <para>
		///         The split is the message's own: mail is written on the GAME connection, where the mailbox
		///         transactions live, but the notification rides <b>CSPlayerInterface</b> - the router tier - which is
		///         how it reaches a player who is nowhere near a mailbox. Without it a player only learns about mail by
		///         opening one.
		///     </para>
		/// </summary>
		Task<bool> NotifyMailReceived(uint recipientId, ulong mailId);

		/// <summary>
		///     Credit <paramref name="amountTin" /> to the guild <paramref name="characterId" /> belongs to - the deposit
		///     half of the guild bank (#654), and the exact mirror of the withdrawal's
		///     <c>IGameServerDispatcher.CreditCharacterCash</c>. Returns false when the character is in no guild, or the
		///     guild is not on this realm's CS tier, so the caller can refund the wallet it has already debited.
		///     <para>
		///         The split is the client's own: <c>GuildTransferMoney</c> arrives on the GAME connection because that is
		///         where the wallet lives, while <c>Guild.Cash</c> lives here. Whoever holds the money does the debit
		///         first and the other side is credited across this seam - see <c>GuildService.DepositToGuildBank</c> for
		///         why that order is the safe one for a money handler.
		///     </para>
		/// </summary>
		Task<bool> DepositToGuildBank(uint characterId, ulong amountTin);
	}

	/// <summary>A started match's identity as the world entry needs it: the instance key and the running clock.</summary>
	public readonly record struct PvpMatchWorldEntry(uint MatchId, uint SecondsSinceStarted);
}
