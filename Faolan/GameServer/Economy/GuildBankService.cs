using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Constants;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	// The GUILD BANK's item vault (#654). The MONEY half lives on the CS tier (`GuildService.DepositToGuildBank` /
	// `HandleGuildWithdrawMoney`) because a balance change is pushed to every online member; the ITEM half is
	// ordinary inventory and stays here, which is also where the capture puts its reply - on the game connection.
	//
	// Decoded end to end from [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:45:09-09:46:12],
	// with the player narrating each step in `/say` ("just deposited 1g to guild bank", "deposidet an item",
	// "withdrew it"):
	//
	//   open      one banker interaction pushes the personal bank's slot registrations, DispatchOpenTradepost AND
	//             DispatchGuildBankData in the same instant. There is no `OpenGuildBank` request on the wire.
	//   move      an ORDINARY MoveInventoryItemIIR (0x2A8A1B49) with the vault on one end -> the same six values
	//             echoed back raw. ⚠️ Not a message of its own: the id looks novel in a dump and is not, so the
	//             listener routes on the CONTAINER rather than on a second opcode.
	//
	// ⚠️ The vault is owned by the GUILD, not by a member: container kind 109, whose wire `instance` is the guild
	// id. That is why `ItemInstance` grew `OwnerGuildId` rather than reusing the character owner - an item in the
	// vault has to survive the member who deposited it leaving.
	public sealed class GuildBankService
	{
		// The captured vault header: 100 slots, none disabled. `DispatchGuildBankData`'s container reads
		// `{ type 109, instance <guild> } slots 100, 0` and the vault was empty, so the CAPACITY is observed and the
		// disabled count is observed-at-zero rather than known to be always zero.
		private const uint VaultDisabledSlots = 0;
		private const uint VaultSlots = 100;

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IEconomyOutbound _outbound;

		private readonly ICsPlayerAgentDispatcher _csPlayerAgentDispatcher;

		public GuildBankService(IDatabaseRepository database, IEconomyOutbound outbound, ILogger logger, ICsPlayerAgentDispatcher csPlayerAgentDispatcher)
		{
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;
			_database = database;
			_outbound = outbound;
			_logger = logger;
		}

		// Pushes the vault, unasked, as part of opening the banker. A character in no guild gets nothing - retail
		// has no observed "you have no guild bank" message and inventing one would put a string on the wire that no
		// capture has.
		public async Task SendVault(INetworkClient client)
		{
			if (client.Session.Character.GuildId is not { } guildId)
				return;

			var stored = await _database.GetGuildBankItems(guildId);

			// The container is built by the SAME builder the personal bank and the login snapshot use, with the
			// GUILD id where a character id would go - which is the whole difference between the two on the wire.
			var vault = FullCharacterRoots.Container(ContainerKind.GuildBank, VaultSlots, VaultDisabledSlots, guildId, stored);
			await _outbound.SendGuildBankData(client, vault);
		}

		// Whether this ordinary inventory drag is actually a guild-bank move - i.e. the vault is one of its ends.
		// Static and on this class because it is the ROUTING question the listener asks before it has a service,
		// and putting it here keeps "what counts as a vault move" in one place with the code that performs one.
		public static bool IsVaultMove(ProtoMoveInventoryItemIIR move)
		{
			return move?.SourceContainerId?.Type == (uint)ContainerKind.GuildBank ||
				move?.DestinationContainerId?.Type == (uint)ContainerKind.GuildBank;
		}

		// One item, into or out of the vault. `move` is the client's ordinary MoveInventoryItemIIR payload.
		//
		// A refusal is SILENT and the item does not move, which is the safe side of every unknown here: retail's
		// reply is a bare ECHO of the request, so it says only "it happened" and there is no observed refusal
		// message to send instead. The client re-reads the vault on the next open.
		public async Task<bool> Move(INetworkClient client, ProtoMoveInventoryItemIIR request)
		{
			var character = client.Session.Character;
			if (character.GuildId is not { } guildId || request.SourceContainerId == null || request.DestinationContainerId == null)
				return false;

			var move = new GuildBankMove(
				new GuildBankEnd((ContainerKind)request.SourceContainerId.Type, request.SourceContainerId.Instance), (uint)request.SourceItemPos,
				new GuildBankEnd((ContainerKind)request.DestinationContainerId.Type, request.DestinationContainerId.Instance), (uint)request.DestinationItemPos);

			// ⚠️ The guild end must name THIS character's guild. The id is client-supplied, and without this a
			// member could address another guild's vault by sending its id - the one place in this message where
			// trusting the client would hand out other people's items.
			var vaultEnd = move.From.Container == ContainerKind.GuildBank
				? move.From
				: move.To;
			if (vaultEnd.Container != ContainerKind.GuildBank || vaultEnd.Instance != guildId)
			{
				_logger.LogInformation("Guild-bank move refused: {Character} addressed vault instance {Asked}, their guild is {Guild}.", character.Name, vaultEnd.Instance, guildId);
				return false;
			}

			if (!await _database.MoveGuildBankItem(character.Id, guildId, move.From.Container, (int)move.FromSlot, move.To.Container, (int)move.ToSlot))
			{
				_logger.LogInformation("Guild-bank move not persisted: {From}[{FromSlot}] -> {To}[{ToSlot}] for {Character}.", move.From.Container, move.FromSlot, move.To.Container,
					move.ToSlot, character.Name);
				return false;
			}

			await _outbound.SendGuildBankMove(client, move);
			_logger.LogInformation("Guild bank: {Character} moved {From}[{FromSlot}] -> {To}[{ToSlot}].", character.Name, move.From.Container, move.FromSlot, move.To.Container,
				move.ToSlot);

			return true;
		}
		// The deposit half of the guild bank (#654), the exact inverse of CreditCharacterCash above: there the CS tier
		// debits the guild and this tier credits the wallet, here this tier debits the wallet and the CS tier credits
		// the guild. Both do the debit on the side that HOLDS the money, so a failure between the two steps loses the
		// transfer rather than minting one.
		//
		// A payer who cannot afford it gets retail's own named refusal (`DispatchGuildNotEnoughCash`) rather than
		// silence: unlike the requests this codebase deliberately leaves unanswered, the client CAN legitimately send
		// this one - it does not know the wallet moved - and the client has a message for exactly this case.
		public async Task HandleGuildDeposit(INetworkClient client, GuildTransferMoney request)
		{
			var character = client.Session.Character;
			if (character == null || request.AmountTin == 0)
				return;

			if (request.AmountTin > character.Money)
			{
				_logger.LogInformation("GuildTransferMoney refused: {Character} offered {Amount} tin of their {Balance}.", character.Name, request.AmountTin, character.Money);
				// ⚠️ THE ENDPOINTS ARE THE WEAK PART, and they are not recovered from anything. The client's method
				// table gives the METHOD and its PARAMETERS; it says nothing about the RpcHeader, whose sender is a
				// server OBJECT id we only ever learn from a capture - and no capture performs a guild-bank deposit.
				//
				// Worse, the instance is demonstrably NOT constant per interface: captured GameCharInterface replies
				// arrive from GameCharAgent{0x58} (the login/world-handshake family) AND from GameCharAgent{0x10}
				// (the trade family). Which server object owns the guild bank is unknown, so 0x10 is a COIN FLIP
				// taken from the nearest neighbour, exactly like the quest-share dispatches below.
				//
				// That is the failure mode #101's team-invite window cost a day to: a wrong sender looks like a wrong
				// body and is not one. If this refusal does not render in-client, suspect this line before the codec.
				// [V0: the guild-bank reply endpoints]
				await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GuildNotEnoughCashCodec.Method, GuildNotEnoughCashCodec.Build(request.AmountTin)));
				return;
			}

			character.Money -= request.AmountTin;
			await _database.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			if (await _csPlayerAgentDispatcher.DepositToGuildBank(character.Id, request.AmountTin))
				return;

			// Nobody could take it - put it back rather than leave it nowhere (the withdrawal's refund, mirrored).
			character.Money += request.AmountTin;
			await _database.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
			_logger.LogWarning("GuildTransferMoney rolled back: {Character}'s guild is not on this realm's CS tier.", character.Name);
		}
	}
}
