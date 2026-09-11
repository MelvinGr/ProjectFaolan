using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The guild bank's item vault.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The money half lives on the CS tier (<c>GuildService.DepositToGuildBank</c> /
	///         <c>HandleGuildWithdrawMoney</c>), because a balance change is pushed to every online member; the item
	///         half is ordinary inventory and stays here, which is also where the capture puts its reply - on the game
	///         connection.
	///     </para>
	///     <para>
	///         Decoded end to end from [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json
	///         09:45:09-09:46:12], with the player narrating each step in <c>/say</c>. Opening is one banker
	///         interaction, which pushes the personal bank's slot registrations, DispatchOpenTradepost and
	///         DispatchGuildBankData in the same instant - there is no <c>OpenGuildBank</c> request on the wire. A
	///         move is an ordinary MoveInventoryItemIIR (0x2A8A1B49) with the vault on one end, answered by the same
	///         six values echoed back raw, so the listener routes on the container rather than on a second opcode.
	///     </para>
	///     <para>
	///         The vault is owned by the guild rather than by a member: container kind 109, whose wire
	///         <c>instance</c> is the guild id. That is why <c>ItemInstance</c> carries <c>OwnerGuildId</c> rather
	///         than reusing the character owner - an item in the vault has to survive the member who deposited it
	///         leaving.
	///     </para>
	/// </remarks>
	public sealed class GuildBankService
	{
		// The captured vault header: 100 slots, none disabled. `DispatchGuildBankData`'s container reads
		// `{ type 109, instance <guild> } slots 100, 0` and the vault was empty, so the capacity is observed and the
		// disabled count is observed-at-zero rather than known to be always zero.
		private const uint VaultDisabledSlots = 0;
		private const uint VaultSlots = 100;

		private readonly ICharacterInventoryRepository _database;
		private readonly ILogger _logger;
		private readonly IEconomyOutbound _outbound;

		private readonly ICsPlayerAgentDispatcher _csPlayerAgentDispatcher;

		public GuildBankService(ICharacterInventoryRepository database, IEconomyOutbound outbound, ILogger logger, ICsPlayerAgentDispatcher csPlayerAgentDispatcher)
		{
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;
			_database = database;
			_outbound = outbound;
			_logger = logger;
		}

		/// <summary>
		///     Pushes the vault, unasked, as part of opening the banker. A character in no guild gets nothing: retail
		///     has no observed "you have no guild bank" message, and inventing one would put a string on the wire that
		///     no capture has.
		/// </summary>
		public async Task SendVault(INetworkClient client)
		{
			if (client.Session.Character.GuildId is not { } guildId)
				return;

			var stored = await _database.GetGuildBankItems(guildId);

			// The container is built by the same builder the personal bank and the login snapshot use, with the guild
			// id where a character id would go - which is the whole difference between the two on the wire.
			var vault = FullCharacterRoots.Container(ContainerKind.GuildBank, VaultSlots, VaultDisabledSlots, guildId, stored);
			await _outbound.SendGuildBankData(client, vault);
		}

		/// <summary>
		///     Whether this ordinary inventory drag is actually a guild-bank move - that is, whether the vault is one
		///     of its ends.
		/// </summary>
		/// <remarks>
		///     Static and on this class because it is the routing question the listener asks before it has a service,
		///     which keeps "what counts as a vault move" beside the code that performs one.
		/// </remarks>
		public static bool IsVaultMove(ProtoMoveInventoryItemIIR move)
		{
			return move?.SourceContainerId?.Type == (uint)ContainerKind.GuildBank ||
				move?.DestinationContainerId?.Type == (uint)ContainerKind.GuildBank;
		}

		/// <summary>
		///     One item, into or out of the vault, from the client's ordinary MoveInventoryItemIIR payload.
		/// </summary>
		/// <remarks>
		///     A refusal is silent and the item does not move, which is the safe side of every unknown here: retail's
		///     reply is a bare echo of the request, so it says only that the move happened, and there is no observed
		///     refusal message to send instead. The client re-reads the vault on the next open.
		/// </remarks>
		public async Task<bool> Move(INetworkClient client, ProtoMoveInventoryItemIIR request)
		{
			var character = client.Session.Character;
			if (character.GuildId is not { } guildId || request.SourceContainerId == null || request.DestinationContainerId == null)
				return false;

			var move = new GuildBankMove(
				new GuildBankEnd((ContainerKind)request.SourceContainerId.Type, request.SourceContainerId.Instance), (uint)request.SourceItemPos,
				new GuildBankEnd((ContainerKind)request.DestinationContainerId.Type, request.DestinationContainerId.Instance), (uint)request.DestinationItemPos);

			// The guild end must name this character's guild. The id is client-supplied, and without this check a
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
		/// <summary>
		///     The deposit half of the guild bank, the inverse of the withdrawal: there the CS tier debits the guild
		///     and this tier credits the wallet, here this tier debits the wallet and the CS tier credits the guild.
		/// </summary>
		/// <remarks>
		///     Both do the debit on the side that holds the money, so a failure between the two steps loses the
		///     transfer rather than minting one. A payer who cannot afford it gets retail's own named refusal
		///     (<c>DispatchGuildNotEnoughCash</c>) rather than silence: the client can legitimately send this request,
		///     because it does not know the wallet moved, and it has a message for exactly this case.
		/// </remarks>
		public async Task HandleGuildDeposit(INetworkClient client, GuildTransferMoney request)
		{
			var character = client.Session.Character;
			if (character == null || request.AmountTin == 0)
				return;

			if (request.AmountTin > character.Money)
			{
				_logger.LogInformation("GuildTransferMoney refused: {Character} offered {Amount} tin of their {Balance}.", character.Name, request.AmountTin, character.Money);
				// The endpoints are the weak part and are recovered from nothing. The client's method table gives the
				// method and its parameters; it says nothing about the RpcHeader, whose sender is a server object id
				// only a capture states - and no capture performs a guild-bank deposit.
				//
				// The instance is demonstrably not constant per interface: captured GameCharInterface replies arrive
				// from GameCharAgent{0x58}, the login and world-handshake family, and from GameCharAgent{0x10}, the
				// trade family. Which server object owns the guild bank is unknown, so 0x10 is taken from the nearest
				// neighbour. A wrong sender looks exactly like a wrong body, so if this refusal does not render
				// in-client, suspect this line before the codec.
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

			// Nobody could take it, so put it back rather than leave it nowhere - the withdrawal's refund, mirrored.
			character.Money += request.AmountTin;
			await _database.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
			_logger.LogWarning("GuildTransferMoney rolled back: {Character}'s guild is not on this realm's CS tier.", character.Name);
		}
	}
}
