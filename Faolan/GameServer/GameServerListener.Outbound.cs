using System;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{

		// IGameServerProvider seam: the pay-out half of a guild-bank withdrawal. The CS tier decides it, because it
		// owns Guild.Cash and the rank ladder, and this tier pays it, because the wallet lives on the live session and
		// the client only learns of a change through this tier's UpdateCash.
		//
		// Refusing when the character is not connected here is the point, not a technicality: the caller debits the
		// guild only if this returns true, so a payout that nobody could receive never happens rather than vanishing.
		public async Task<bool> CreditCharacterCash(uint characterId, ulong amountTin)
		{
			if (ClientForCharacter(characterId) is not { Session.Character: { } character } client)
				return false;

			// The wallet is the player's live state, so it moves on their zone's loop like any other change to them.
			await _simulation.RunForPlayer(client, async () =>
			{
				character.Money += amountTin;
				await Database.SetCharacterMoney(character.Id, character.Money);
				await _economyOutbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
			});
			Logger.LogInformation("CreditCharacterCash: {Character} received {Amount} tin (wallet {Balance}).", character.Name, amountTin, character.Money);
			return true;
		}

		// IGameServerProvider seam: pushes the guild tag onto the character's live world connection, if it is
		// connected to this listener. Used by the CsPlayerAgent guild flow (accept/create/leave) so the in-world
		// tag updates without a relog.
		public async Task<bool> SendGuildTag(uint characterId, string guildName)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(GameWire.BuildGuildTag(client, characterId, guildName ?? ""));
			Logger.LogInformation("SendGuildTag: set in-world guild tag of {CharacterId:X8} to \"{GuildName}\".", characterId, guildName);
			return true;
		}

		// IGameServerProvider seam: the world-connection half of a team change. TeamService owns the roster, which
		// spans the CS tier and this one; this only puts the record on the wire for a member connected here.
		public async Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined)
		{
			var client = ClientForCharacter(recipientId);
			if (client?.Session.Account == null)
				return false;

			await WorldIir.Send(client, GroupMembershipCodec.Build(joined
				? new AddToGroupIIR(memberId, teamId, leaderId)
				: new RemoveFromGroupIIR(memberId, teamId)));
			return true;
		}

		// IGameServerProvider seam: the two PvP-minigame messages that end a match. The match lives on the CS tier -
		// PvpMatchService owns the roster, the clock and the return trip - but every Minigame* dispatch is a
		// GameCharInterface message and so travels on this connection, the same split the match-start clock crosses in
		// WorldEntryService. Client-recovered and uncaptured: no captured session finishes a minigame
		// (docs/re/minigame-massivepvp-messages.md).
		public async Task<bool> SendMinigameMatchEnded(uint characterId, uint winningSide, MinigameResult yourResult)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchMinigameMatchEnded,
				s => s.WriteUInt32(winningSide).WriteUInt32((uint)yourResult)));
			Logger.LogInformation("PvP minigame: told {CharacterId:X8} the match ended - winning side {Side}, result {Result}.", characterId, winningSide, yourResult);
			return true;
		}

		public async Task<bool> SendMinigameForceStop(uint characterId, string message)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchForceStopMinigame,
				s => s.WriteString(message)));
			Logger.LogInformation("PvP minigame: force-stopped for {CharacterId:X8} - \"{Message}\".", characterId, message);
			return true;
		}

		// 0x4F57DC08 GameTime - the live world clock. Both fields are generated: the resync anchor from the wall clock
		// and the time of day from it at 5 game seconds per real second, wrapping every 86400 (see GameTimeCodec for
		// where those come from). `_clock.OffsetGameSeconds` shifts the phase for the `.time` GM command.
		internal Rpc BuildGameTime(INetworkClient client)
		{
			return GameWire.BuildGameTime(client, DateTimeOffset.UtcNow, _clock.OffsetGameSeconds, _clock.Rate);
		}


		// Sends the proven captured system line - the test bootstrap for the ChatTextIIR path until a literal-text
		// remoteformat template lands.


		// Which of the two vitals blocks to send is decided by what moved, and both forms are retail's.
		//
		// `EntityVitals.ToCurrentsBlock` - the three current ids - is for a push where only a current moved: a hit, a
		// cast's mana cost, the regen tick, fall damage. It is what retail overwhelmingly sends
		// (`--report vitals-shape`: 3,694 of the 4,900 blocks on a non-own handle carry exactly
		// {HealthCurrent, StaminaCurrent, ManaCurrent}).
		//
		// `EntityVitals.ToVitalsBlock` - current and max - is for a push where a max moved: a level-up, a gear or buff
		// change to a pool, a GM `.set`. Use CombatService.ReseedFromMaxStats(client).ToVitalsBlock() there.
		//
		// The rule is "what moved", neither "always send everything" nor "always send currents". The max ids reach the
		// client only when something sends them, so sending currents alone leaves every later max change invisible and
		// the bar stuck on its world-entry seed. Sending all six on every swing is the opposite error: three fabricated
		// records per entity per hit on the hottest path in the game, and for an NPC - which has no class curve, so
		// ClassBasePools is null - the max ids fall back to a computed ceiling, which is not the quantity that slot
		// holds.

		// Sends a pre-framed world IIR body ([len]-prefixed) on the world conversation (opcode 0x2000).

	}
}
