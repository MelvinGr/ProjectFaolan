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
}
