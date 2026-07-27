using Faolan.Core;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Economy
{
	// PLAYER-TO-PLAYER trade (Gitea #101) - distinct from the vendor/auction paths in this folder, and from
	// KnubotStartTradeCodec (which opens an NPC vendor). Decoded from login_2-clients_group-trade-friend, where both
	// clients are on tape, so every message is seen from BOTH sides; tools/capture/decode_team_trade_friend.py reads
	// them all to their exact end.
	//
	// The captured sequence, once (the capture does it twice - cash-only, then items both ways):
	//
	//   s2c 0x2068 DispatchRequestTrade        [ProtoID32 requester]   "X wants to trade"
	//   c2s 0x2065 StartTrade                  [ProtoID32 partner]     the target opens the window
	//   s2c 0x2069 DispatchStartTrade          [ProtoID32 partner]     to BOTH - each carrying the OTHER party
	//   c2s 0x2067 SetTradeCash                [u64 tin]
	//   s2c 0x206E DispatchPartnerUpdatedCash  [u64 tin]               to the partner
	//   c2s 0x2068 AddTradeItem                [u32 container][u32 owner][u32 slot][u32 count]
	//   s2c 0x2070 DispatchTradeAddedItem      [u32 len][item snapshot][u32 count]   to the adder
	//   s2c 0x206F DispatchTradePartnerAddedItem  (same body)                        to the partner
	//   c2s 0x206B AcceptTrade                 (empty)
	//   s2c 0x206C DispatchPartnerAcceptedTrade [ProtoID32 partner]    to the OTHER side
	//   s2c 0x206B (Reserved0x206B)            (empty)                 acks YOUR accept
	//   s2c 0x206D (Reserved0x206D)            (empty)                 to BOTH: the trade COMMITS
	//                                          then the TradeCompletedSuccessfully feedback overlay (0x8D5A8B6B)
	//
	// ⚠️ c2s 0x2068 is AddTradeItem while s2c 0x2068 is DispatchRequestTrade - the same method index on the two
	// interfaces, which is exactly how 16-bit method indices work (per interface, per direction). Do not "unify" them.
	//
	// No `Opcode` constant: these are RPC methods, not 0x2000 IIR types.
	public static class PlayerTradeCodec
	{
		// Every ProtoID32-bodied message in the flow: DispatchRequestTrade, DispatchStartTrade,
		// DispatchPartnerAcceptedTrade. Which one it is comes from the OPCODE, so one builder serves all three.
		public static byte[] BuildCharacterRef(uint characterId)
		{
			return new FaolanStream().WriteUInt32(EntityClass.Character).WriteUInt32(characterId).ToArray();
		}

		// s2c DispatchPartnerUpdatedCash (0x206E) - and the same body the client sends as SetTradeCash.
		public static byte[] BuildCash(ulong tin)
		{
			return new FaolanStream().WriteUInt64(tin).ToArray();
		}

		// s2c DispatchTradeAddedItem (0x2070) / DispatchTradePartnerAddedItem (0x206F): a length-prefixed item
		// snapshot - the same record the inventory paths build - then the stack count.
		public static byte[] BuildAddedItem(byte[] itemSnapshot, uint count)
		{
			return new FaolanStream().WriteArrayUInt32Length(itemSnapshot).WriteUInt32(count).ToArray();
		}

		// c2s AddTradeItem (0x2068): which of the sender's items goes on the table, and where in the trade WINDOW it
		// lands. The client's own signature is AddTradeItem(const ID32& i_cIventoryID, int i_nItemPos, int i_nDestPos) -
		// so the ID32 is the source container (104 = the backpack in both captured adds, paired with the sender's own
		// character id), `ItemPos` the bag slot, and the last u32 is `DestPos`, the slot in the trade window.
		//
		// ⚠️ That last field was read as a COUNT, which happened to work only because the capture's single add carried
		// destPos 1 for a stack of 1. There is no count in the message at all: the whole stack moves, so the quantity
		// comes from the inventory row. A destPos of 0 read as a count would have moved zero of the item.
		public static TradeItemRequest ParseAddItem(FaolanStream s)
		{
			return new TradeItemRequest(s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32(), s.ReadUInt32());
		}

		public readonly record struct TradeItemRequest(uint Container, uint Owner, uint ItemPos, uint DestPos);
	}
}
