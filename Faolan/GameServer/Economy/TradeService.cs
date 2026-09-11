using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     Player-to-player trade, decoded in docs/subsystems/teams-trade-friends.md from
	///     login_2-clients_group-trade-friend, where both clients are on tape so every message is seen from both sides.
	/// </summary>
	/// <remarks>
	///     Distinct from the vendor, bank and auction paths beside it, and from KnubotStartTrade, which opens an NPC
	///     vendor. A trade is live state, like a team and unlike a friend list: it cannot outlive either connection, so
	///     nothing here is persisted - only the outcome is, and only at the commit. The session is symmetric, two sides
	///     each with offered items, offered cash and an accept flag, which is what keeps the wire handlers to "record
	///     it on my side, tell the other side".
	/// </remarks>
	public sealed class TradeService
	{
		private readonly ConcurrentDictionary<uint, TradeSession> _byCharacter = [];
		private readonly ICharacterInventoryRepository _inventory;
		private readonly ICharacterRepository _characters;
		private readonly ILogger _logger;
		private readonly ITradeOutbound _outbound;

		public TradeService(ICharacterInventoryRepository inventory, ICharacterRepository characters, ILogger logger, ITradeOutbound outbound)
		{
			_inventory = inventory;
			_characters = characters;
			_logger = logger;
			_outbound = outbound;
		}

		// The pending request window, requester to target: set by the BCC behaviour 0x16 and consumed by StartTrade.
		private readonly ConcurrentDictionary<uint, uint> _pendingRequests = [];

		/// <summary>
		///     BCC behaviour 0x16 targeting another player: "I want to trade with you". The target is carried in the
		///     BCC's CommandMessage (field 10, a ProtoID32 whose instance is the target character id).
		/// </summary>
		/// <remarks>
		///     Retail answers the BCC with the usual hold-echo and sends DispatchRequestTrade to the target, carrying
		///     the requester's id.
		/// </remarks>
		public async Task Request(uint requesterId, uint targetId)
		{
			if (_byCharacter.ContainsKey(requesterId) || _byCharacter.ContainsKey(targetId))
			{
				_logger.LogInformation("Trade: {Requester:X8} -> {Target:X8} refused, one of them is already trading.", requesterId, targetId);
				return;
			}

			_pendingRequests[targetId] = requesterId;
			await _outbound.SendTradeRequest(targetId, requesterId);
			_logger.LogInformation("Trade: {Requester:X8} asked {Target:X8} to trade.", requesterId, targetId);
		}

		/// <summary>
		///     c2s StartTrade (0x2065): the target opens the window, which is what creates the session. Retail then
		///     sends DispatchStartTrade to both, each carrying the other party, so each client knows who it is trading
		///     with.
		/// </summary>
		public async Task Start(uint accepterId, uint partnerId)
		{
			// The requester is taken from the server's own pending map rather than from the body, so a forged
			// StartTrade cannot open a window on someone who never asked - the same rule the team accept follows.
			if (!_pendingRequests.TryRemove(accepterId, out var requesterId) || requesterId != partnerId)
			{
				_logger.LogWarning("Trade: StartTrade from {Character:X8} for {Partner:X8} with no matching request.", accepterId, partnerId);
				return;
			}

			var session = new TradeSession(requesterId, accepterId);
			_byCharacter[requesterId] = session;
			_byCharacter[accepterId] = session;

			await _outbound.SendTradeStarted(accepterId, requesterId);
			await _outbound.SendTradeStarted(requesterId, accepterId);
			_logger.LogInformation("Trade: {A:X8} <-> {B:X8} opened.", requesterId, accepterId);
		}

		/// <summary>
		///     c2s SetTradeCash (0x2067): what this player is putting on the table. Retail echoes it to the partner
		///     only, since the sender's own client already shows what was typed.
		/// </summary>
		public async Task SetCash(uint characterId, ulong tin)
		{
			if (!TryGetSide(characterId, out var session, out var side))
				return;

			// Clamp to what the player actually has, so the commit cannot be asked to move money that does not exist.
			// The client presumably refuses first, but that cannot be relied on.
			var character = await _characters.GetCharacter(characterId);
			side.Cash = character == null
				? 0
				: System.Math.Min(tin, character.Money);

			session.ResetAccepts(); // any change invalidates both accepts - otherwise one side could alter the offer after the other agreed
			await _outbound.SendPartnerCash(session.Other(characterId).CharacterId, side.Cash);
		}

		/// <summary>
		///     c2s AddTradeItem (0x2068): one of the player's backpack slots goes on the table.
		/// </summary>
		/// <remarks>
		///     The reply carries the item snapshot, so the partner's window can render it: DispatchTradeAddedItem to
		///     the offerer and DispatchTradePartnerAddedItem to the partner, the same body under a different opcode per
		///     recipient. The offer is keyed by the destination position - the slot in the trade window - because that
		///     is what RemoveTradeItem and MoveTradeItemInternal address, and one bag slot could otherwise be offered
		///     into two window slots.
		/// </remarks>
		public async Task AddItem(uint characterId, uint itemPos, uint destPos)
		{
			if (!TryGetSide(characterId, out var session, out var side))
				return;

			var offer = await _inventory.GetCharacterItemSlot(characterId, ContainerKind.Bag, (int)itemPos);
			if (offer == null)
			{
				_logger.LogWarning("Trade: {Character:X8} offered empty bag slot {Slot}.", characterId, itemPos);
				return;
			}

			// The message carries no quantity, so the whole stack goes on the table.
			side.Items[destPos] = new OfferedItem(itemPos, offer, offer.Count);
			session.ResetAccepts();

			var partnerId = session.Other(characterId).CharacterId;
			await _outbound.SendTradeItem(characterId, offer, destPos, offer.Count, true);
			await _outbound.SendTradeItem(partnerId, offer, destPos, offer.Count, false);
		}

		/// <summary>
		///     c2s RemoveTradeItem (0x2069): take an item back off the table.
		/// </summary>
		/// <remarks>
		///     The mirror of the add - the same two s2c halves to the same two recipients, DispatchTradeRemovedItem to
		///     the owner and DispatchTradePartnerRemovedItem to the partner, carrying the window slot. Without them the
		///     partner's window goes on rendering an item that is no longer on the table.
		/// </remarks>
		public async Task RemoveItem(uint characterId, uint destPos)
		{
			if (!TryGetSide(characterId, out var session, out var side))
				return;

			if (!side.Items.Remove(destPos))
				return; // nothing there - say nothing rather than tell the partner to clear a slot that never had an item

			session.ResetAccepts();

			var partnerId = session.Other(characterId).CharacterId;
			await _outbound.SendTradeItemRemoved(characterId, destPos, true);
			await _outbound.SendTradeItemRemoved(partnerId, destPos, false);
		}

		/// <summary>
		///     c2s MoveTradeItemInternal (0x206A): the player drags an offered item to another slot of the trade
		///     window.
		/// </summary>
		/// <remarks>
		///     Pure bookkeeping - nothing leaves the offer, so the accepts are not reset - but the keys have to follow,
		///     or a later RemoveTradeItem addresses the wrong item.
		/// </remarks>
		public void MoveItem(uint characterId, uint fromPos, uint toPos)
		{
			if (!TryGetSide(characterId, out _, out var side) || !side.Items.TryGetValue(fromPos, out var moved))
				return;

			side.Items.Remove(fromPos);
			if (side.Items.TryGetValue(toPos, out var displaced))
				side.Items[fromPos] = displaced; // a swap, not an overwrite - the other item goes where this one came from

			side.Items[toPos] = moved;
		}

		/// <summary>
		///     c2s AcceptTrade (0x206B, empty): this player agrees to the table as it stands.
		/// </summary>
		/// <remarks>
		///     Retail acks the accepter's own accept (the empty 0x206B), tells the partner
		///     (DispatchPartnerAcceptedTrade), and once both have accepted commits the exchange and sends the empty
		///     0x206D to both, followed by the TradeCompletedSuccessfully overlay.
		/// </remarks>
		public async Task Accept(uint characterId)
		{
			if (!TryGetSide(characterId, out var session, out var side))
				return;

			side.Accepted = true;
			await _outbound.SendAcceptAck(characterId);
			await _outbound.SendPartnerAccepted(session.Other(characterId).CharacterId, characterId);

			if (!session.Left.Accepted || !session.Right.Accepted)
				return;

			await Commit(session);
		}

		/// <summary>
		///     Both sides agreed: move everything, then tell both it is done.
		/// </summary>
		/// <remarks>
		///     The database writes happen before the wire, so a failure cannot leave the clients believing in a trade
		///     that did not happen.
		/// </remarks>
		private async Task Commit(TradeSession session)
		{
			await Exchange(session.Left, session.Right);
			await Exchange(session.Right, session.Left);

			foreach (var side in new[] { session.Left, session.Right })
			{
				_byCharacter.TryRemove(side.CharacterId, out _);
				await _outbound.SendTradeCompleted(side.CharacterId);
			}

			_logger.LogInformation("Trade: {A:X8} <-> {B:X8} committed ({AItems} + {ATin} tin for {BItems} + {BTin} tin).", session.Left.CharacterId, session.Right.CharacterId,
				session.Left.Items.Count, session.Left.Cash, session.Right.Items.Count, session.Right.Cash);
		}

		/// <summary>
		///     Hands one side's offer to the other.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The delivery goes through the shared grant, which is what makes an item received in a trade advance
		///         a collect objective and push its own live add. The grant re-queries the receiver's rows per call,
		///         which is what lets a multi-item trade see the rows the previous iteration wrote.
		///     </para>
		///     <para>
		///         A trade hands the item over rather than minting one: <c>TransferItemToCharacter</c> moves the
		///         instance to the receiver, so the crafter, the stack and the socketed gems travel with it - and the
		///         offer window is built from that same instance, so it advertises exactly what the exchange
		///         delivers.
		///     </para>
		///     <para>
		///         There is no clear-the-source step: the move is the transfer, so there is no window in which the item
		///         exists twice, and a failed transfer - receiver offline, bag full - leaves it with the giver
		///         untouched. Dropping it on the floor would be worse than not trading it.
		///     </para>
		/// </remarks>
		private async Task Exchange(TradeSide from, TradeSide to)
		{
			foreach (var (_, offered) in from.Items)
			{
				if (await _outbound.TransferItemToCharacter(to.CharacterId, offered.Instance) == null)
				{
					_logger.LogWarning("Trade: {To:X8} could not receive {Item}; it stays with {From:X8}.", to.CharacterId, offered.Instance.Template.Name, from.CharacterId);
					continue;
				}

				// Tell the giver's client. Moving the row is not enough, because the client renders what it last knew,
				// so without this a traded-away item sits visibly in the giver's bag until relog. The receiver's side
				// is the transfer's own live add.
				await _outbound.SendItemRemoved(from.CharacterId, offered.ItemPos);
			}

			if (from.Cash == 0)
				return;

			var giver = await _characters.GetCharacter(from.CharacterId);
			var receiver = await _characters.GetCharacter(to.CharacterId);
			if (giver == null || receiver == null)
				return;

			// Re-clamp at commit time: the offer was clamped when it was made, but the giver may have spent since.
			var moved = System.Math.Min(from.Cash, giver.Money);
			await _inventory.SetCharacterMoney(from.CharacterId, giver.Money - moved);
			await _inventory.SetCharacterMoney(to.CharacterId, receiver.Money + moved);
			await _outbound.SendWalletUpdate(from.CharacterId, giver.Money - moved);
			await _outbound.SendWalletUpdate(to.CharacterId, receiver.Money + moved);
		}

		/// <summary>
		///     Either side walking away - a cancel, a logout, a map change - ends the session for both.
		/// </summary>
		/// <remarks>
		///     Nothing has moved at this point, since the exchange only happens on the double accept, so there is
		///     nothing to roll back.
		/// </remarks>
		public void Abandon(uint characterId)
		{
			_pendingRequests.TryRemove(characterId, out _);
			if (!_byCharacter.TryRemove(characterId, out var session))
				return;

			_byCharacter.TryRemove(session.Other(characterId).CharacterId, out _);
			_logger.LogInformation("Trade: {A:X8} <-> {B:X8} abandoned by {Who:X8}.", session.Left.CharacterId, session.Right.CharacterId, characterId);
		}

		public bool IsTrading(uint characterId)
		{
			return _byCharacter.ContainsKey(characterId);
		}

		private bool TryGetSide(uint characterId, out TradeSession session, out TradeSide side)
		{
			if (_byCharacter.TryGetValue(characterId, out session!))
			{
				side = session.SideOf(characterId);
				return true;
			}

			side = null!;
			_logger.LogWarning("Trade: {Character:X8} sent a trade message with no open session.", characterId);
			return false;
		}


		// ItemPos is the giver's bag slot; the dictionary key is the window slot it was offered into. The offer holds
		// the item instance rather than just its template, because the offer message names it by its own id and
		// restates its sockets, and the exchange moves this exact row to the receiver.
		private sealed record OfferedItem(uint ItemPos, ItemInstance Instance, uint Count);

		private sealed class TradeSide(uint characterId)
		{
			public uint CharacterId { get; } = characterId;
			public Dictionary<uint, OfferedItem> Items { get; } = [];
			public ulong Cash { get; set; }
			public bool Accepted { get; set; }
		}

		private sealed class TradeSession(uint left, uint right)
		{
			public TradeSide Left { get; } = new TradeSide(left);
			public TradeSide Right { get; } = new TradeSide(right);

			public TradeSide SideOf(uint characterId)
			{
				return Left.CharacterId == characterId
					? Left
					: Right;
			}

			public TradeSide Other(uint characterId)
			{
				return Left.CharacterId == characterId
					? Right
					: Left;
			}

			// Any change to either offer clears both accepts, so nobody can agree to one table and receive another.
			public void ResetAccepts()
			{
				Left.Accepted = false;
				Right.Accepted = false;
			}
		}
	}
}
