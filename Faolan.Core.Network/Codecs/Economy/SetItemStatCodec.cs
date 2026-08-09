using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchSetItemStat (s2c 0x201C) - one stat on one container SLOT, 20 bytes:
	//   [u32 containerKind][u32 charInstance][u32 slot][u32 statId][u32 value]
	//
	// ONE message that had TWO emitters (#530 wave 7): `BankIirCodec.WriteBankSlotRegistration` wrote it as
	// `[u32 0x17][u16 0x0F][u16 recordId]` and `GameServerListener.Outbound.BuildBankSlotStatBody` as
	// `[u32 statId][u32 value]`. They are the same 20 bytes - the registration is stat 0x17 carrying the packed record
	// id - so the "registration" was a stat update all along, and the two spellings differed only in where they put
	// the field boundary. The semantic forms are VALUE factories below; byte-exact in TradepostBankStatTests /
	// BankStorageTests.
	public static class SetItemStatCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchSetItemStat;

		// The stat id the bank's per-slot REGISTRATION carries (see SetItemStats.SlotRegistration).
		public const uint SlotRegistrationStat = 0x17;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SetItemStat value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static SetItemStat Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SetItemStatCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static SetItemStat Read(ref SpanReader r)
		{
			return new SetItemStat((ContainerKind)r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, SetItemStat v)
		{
			s.WriteUInt32(v.Container).WriteUInt32(v.CharInstance).WriteUInt32(v.Slot).WriteUInt32(v.StatId).WriteUInt32(v.Value);
		}
	}

	// DispatchSetItemStat (0x201C).
	public sealed record SetItemStat(ContainerKind Container, uint CharInstance, uint Slot, uint StatId, uint Value);

	// The semantic forms, as VALUE factories - they share `(charInstance, slot, <one number>)`, so `Build` overloads
	// could not tell them apart.
	public static class SetItemStats
	{
		// The per-slot registration retail sends once per occupied bank cell BEFORE the BankIIR content: stat 0x17
		// whose value packs `[u16 0x000F][u16 recordId]`. The record id is session-volatile (it matches the capture's
		// range, nothing keys off it).
		public static SetItemStat SlotRegistration(uint charInstance, uint slot, ushort recordId)
		{
			return new SetItemStat(ContainerKind.Bank, charInstance, slot, SetItemStatCodec.SlotRegistrationStat, (0x000Fu << 16) | recordId);
		}

		// A plain stat write on a bank cell - the tradepost "listed"/"price"/"flags" overlay (#368).
		public static SetItemStat BankSlot(uint charInstance, uint slot, uint statId, uint value)
		{
			return new SetItemStat(ContainerKind.Bank, charInstance, slot, statId, value);
		}

		// The item BOUND to its owner (#756): the cell's `Flags` word (stat 0) re-sent with bit 26 set. Retail's own
		// last message of an equip that binds - the stat id is 0, which is why this is a factory rather than a
		// BankSlot call with a 0 passed in: "stat zero" reads as a missing argument, and the WORD is the message.
		// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:47:00.842 - equipped(101) slot 2,
		//  stat 0 = 0x04000001, on an item that had arrived in the bag at 0x00000001]
		public static SetItemStat Bound(ContainerKind container, uint charInstance, uint slot, uint flags)
		{
			return new SetItemStat(container, charInstance, slot, FlagsStat, flags | BoundFlag);
		}

		// stat 0 is the entry's `Flags` word; bit 26 is "bound to whoever holds it".
		private const uint FlagsStat = 0;
		private const uint BoundFlag = 1u << 26;
	}
}
