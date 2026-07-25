using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	// PLAYER-TO-PLAYER trade (#101), decoded in docs/subsystems/teams-trade-friends.md from
	// login_2-clients_group-trade-friend, where both clients are on tape so every message is seen from both sides.
	// Distinct from the vendor/bank/auction paths beside it, and from KnubotStartTradeCodec (which opens an NPC vendor).
	//
	// A trade is LIVE state, like a team and unlike a friend list: it cannot outlive either connection, so nothing here
	// is persisted. What IS persisted is the outcome, and only at the commit.
	//
	// The session is symmetric - two sides, each with offered items, offered cash and an accept flag - so the wire
	// handlers stay small: almost every one is "record it on my side, tell the other side".
	public sealed class TradeService
	{
		private readonly ConcurrentDictionary<uint, TradeSession> _byCharacter = [];
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly ITradeOutbound _outbound;

		public TradeService(IDatabaseRepository database, ILogger logger, ITradeOutbound outbound)
		{
			_database = database;
			_logger = logger;
			_outbound = outbound;
		}

		// The pending REQUEST window: requester -> target, set by the BCC behaviour 0x16 and consumed by StartTrade.
		private readonly ConcurrentDictionary<uint, uint> _pendingRequests = [];

		// BCC behaviour 0x16 targeting another player - "I want to trade with you". The target is carried in the BCC's
		// CommandMessage (field 10, a ProtoID32 whose instance is the target character id). Retail answers the BCC with
		// the usual hold-echo and sends DispatchRequestTrade to the TARGET carrying the REQUESTER's id.
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

		// c2s StartTrade (0x2065): the TARGET opens the window, which is what actually creates the session. Retail then
		// sends DispatchStartTrade to BOTH, each carrying the OTHER party - so each client knows who it is trading with.
		public async Task Start(uint accepterId, uint partnerId)
		{
			// The requester is taken from OUR pending map, not the body, so a forged StartTrade cannot open a window on
			// someone who never asked - the same rule the team accept follows.
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

		// c2s SetTradeCash (0x2067): what I am putting on the table. Retail echoes it to the PARTNER only - my own
		// client already shows what I typed.
		public async Task SetCash(uint characterId, ulong tin)
		{
			if (!TryGetSide(characterId, out var session, out var side))
				return;

			// Clamp to what I actually have, so the commit cannot be asked to move money that does not exist. Retail
			// presumably refuses client-side; we cannot rely on that.
			var character = await _database.GetCharacter(characterId);
			side.Cash = character == null
				? 0
				: System.Math.Min(tin, character.Money);

			session.ResetAccepts(); // any change invalidates both accepts - otherwise one side could alter the offer after the other agreed
			await _outbound.SendPartnerCash(session.Other(characterId).CharacterId, side.Cash);
		}

		// c2s AddTradeItem (0x2068): one of my backpack slots goes on the table. The reply carries the item SNAPSHOT (so
		// the partner's window can render it), to me as DispatchTradeAddedItem and to them as
		// DispatchTradePartnerAddedItem - the same body, different opcode per recipient.
		// Keyed by DESTPOS - the slot in the trade window - because that is what RemoveTradeItem and
		// MoveTradeItemInternal address, and one bag slot could otherwise be offered into two window slots.
		public async Task AddItem(uint characterId, uint itemPos, uint destPos)
		{
			if (!TryGetSide(characterId, out var session, out var side))
				return;

			var row = (await _database.GetCharacterInventory(characterId)).FirstOrDefault(r => r.Container == ContainerKind.Bag && r.Slot == (int)itemPos);
			if (row?.Item == null)
			{
				_logger.LogWarning("Trade: {Character:X8} offered empty bag slot {Slot}.", characterId, itemPos);
				return;
			}

			// The message carries no quantity, so the WHOLE stack goes on the table.
			side.Items[destPos] = new OfferedItem(itemPos, row.Item, row.Count);
			session.ResetAccepts();

			var partnerId = session.Other(characterId).CharacterId;
			await _outbound.SendTradeItem(characterId, row.Item, destPos, row.Count, true);
			await _outbound.SendTradeItem(partnerId, row.Item, destPos, row.Count, false);
		}

		// c2s RemoveTradeItem (0x2069): take an item back off the table. State only - the two s2c halves
		// (DispatchTradeRemovedItem / DispatchTradePartnerRemovedItem) are named in the client's catalog but their
		// OPCODES are not recoverable from it (its s2c numbering is offset against the capture), so the partner's window
		// is not updated yet. See Gitea #427.
		public void RemoveItem(uint characterId, uint destPos)
		{
			if (!TryGetSide(characterId, out var session, out var side))
				return;

			side.Items.Remove(destPos);
			session.ResetAccepts();
		}

		// c2s MoveTradeItemInternal (0x206A): the player drags an offered item to another slot of the trade window. Pure
		// bookkeeping - nothing leaves the offer, so the accepts are NOT reset - but the keys have to follow, or a later
		// RemoveTradeItem would address the wrong item.
		public void MoveItem(uint characterId, uint fromPos, uint toPos)
		{
			if (!TryGetSide(characterId, out _, out var side) || !side.Items.TryGetValue(fromPos, out var moved))
				return;

			side.Items.Remove(fromPos);
			if (side.Items.TryGetValue(toPos, out var displaced))
				side.Items[fromPos] = displaced; // a swap, not an overwrite - the other item goes where this one came from

			side.Items[toPos] = moved;
		}

		// c2s AcceptTrade (0x206B, empty): I agree to the table as it stands. Retail acks MY accept (the empty 0x206B),
		// tells the partner (DispatchPartnerAcceptedTrade), and when BOTH have accepted commits the exchange and sends
		// the empty 0x206D to both followed by the TradeCompletedSuccessfully overlay.
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

		// Both sides agreed: move everything, then tell both it is done. The DB writes happen BEFORE the wire so a
		// failure cannot leave the clients believing in a trade that did not happen.
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

		// Hands `from`'s offer to `to`: clear each offered slot on the giver, place it in the receiver's first free one,
		// and move the cash. A full receiver keeps the item - dropping it on the floor would be worse than not trading it.
		private async Task Exchange(TradeSide from, TradeSide to)
		{
			var receiverRows = await _database.GetCharacterInventory(to.CharacterId);
			var used = receiverRows.Where(r => r.Container == ContainerKind.Bag && r.ItemId != null).Select(r => r.Slot).ToHashSet();

			foreach (var (_, offered) in from.Items)
			{
				var free = Enumerable.Range(0, BackpackSlots).FirstOrDefault(s => !used.Contains(s), -1);
				if (free < 0)
				{
					_logger.LogWarning("Trade: {To:X8} has no free bag slot; {Item} stays with {From:X8}.", to.CharacterId, offered.Item.Name, from.CharacterId);
					continue;
				}

				used.Add(free);
				await _database.SetCharacterInventorySlot(from.CharacterId, ContainerKind.Bag, (int)offered.ItemPos, null);
				await _database.SetCharacterInventorySlot(to.CharacterId, ContainerKind.Bag, free, offered.Item.Id, offered.Count);

				// ...and TELL both clients. Moving the rows is not enough: the client renders what it last knew, so
				// without these the item sits visibly in the giver's bag and never appears in the receiver's until a
				// relog - the DB was right and the game looked broken.
				await _outbound.SendItemRemoved(from.CharacterId, offered.ItemPos);
				await _outbound.SendItemGranted(to.CharacterId, offered.Item, (uint)free, offered.Count);
			}

			if (from.Cash == 0)
				return;

			var giver = await _database.GetCharacter(from.CharacterId);
			var receiver = await _database.GetCharacter(to.CharacterId);
			if (giver == null || receiver == null)
				return;

			// Re-clamp at commit time: the offer was clamped when it was made, but the giver may have spent since.
			var moved = System.Math.Min(from.Cash, giver.Money);
			await _database.SetCharacterMoney(from.CharacterId, giver.Money - moved);
			await _database.SetCharacterMoney(to.CharacterId, receiver.Money + moved);
			await _outbound.SendWalletUpdate(from.CharacterId, giver.Money - moved);
			await _outbound.SendWalletUpdate(to.CharacterId, receiver.Money + moved);
		}

		// Either side walking away (a cancel, a logout, a map change) ends the session for BOTH. Nothing has moved yet at
		// this point - the exchange only happens on the double accept - so there is nothing to roll back.
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

		// The backpack's slot count, matching BaseBackpackSlots on the listener.
		private const int BackpackSlots = 32;

		// ItemPos is the giver's BAG slot; the dictionary key is the window slot it was offered into.
		private sealed record OfferedItem(uint ItemPos, Item Item, uint Count);

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

			// Any change to either offer clears BOTH accepts, so nobody can agree to one table and receive another.
			public void ResetAccepts()
			{
				Left.Accepted = false;
				Right.Accepted = false;
			}
		}
	}
}
