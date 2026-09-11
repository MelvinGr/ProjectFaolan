using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchSetItemStat</c> - one stat on one container slot, 20 bytes:
	///     <c>[u32 containerKind][u32 charInstance][u32 slot][u32 statId][u32 value]</c>.
	/// </summary>
	/// <remarks>
	///     The bank's per-slot "registration" is this message too: stat 0x17 carrying the packed record id. The semantic
	///     forms are value factories below; byte-exact in <c>TradepostBankStatTests</c> and <c>BankStorageTests</c>.
	/// </remarks>
	public static class SetItemStatCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchSetItemStat;

		// The stat id the bank's per-slot registration carries (see SetItemStats.SlotRegistration).
		public const uint SlotRegistrationStat = 0x17;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The DispatchSetItemStat body.</summary>
	public sealed record SetItemStat(ContainerKind Container, uint CharInstance, uint Slot, uint StatId, uint Value);

	/// <summary>
	///     The semantic forms, as value factories: they share <c>(charInstance, slot, one number)</c>, so <c>Build</c>
	///     overloads could not tell them apart.
	/// </summary>
	public static class SetItemStats
	{
		// The per-slot registration retail sends once per occupied bank cell before the BankIIR content: stat 0x17 whose
		// value packs `[u16 0x000F][u16 recordId]`. The record id is session-volatile - it matches the capture's range, and
		// nothing keys off it.
		public static SetItemStat SlotRegistration(uint charInstance, uint slot, ushort recordId)
		{
			return new SetItemStat(ContainerKind.Bank, charInstance, slot, SetItemStatCodec.SlotRegistrationStat, (0x000Fu << 16) | recordId);
		}

		// A plain stat write on a bank cell - the tradepost "listed"/"price"/"flags" overlay.
		public static SetItemStat BankSlot(uint charInstance, uint slot, uint statId, uint value)
		{
			return new SetItemStat(ContainerKind.Bank, charInstance, slot, statId, value);
		}

		// The item bound to its owner: the cell's `Flags` word (stat 0) re-sent with bit 26 set. Retail's own last message
		// of an equip that binds. The stat id is 0, which is why this is a factory rather than a BankSlot call with a 0
		// passed in: "stat zero" reads as a missing argument, and the word is the message.
		// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:47:00.842 - equipped(101) slot 2,
		//  stat 0 = 0x04000001, on an item that had arrived in the bag at 0x00000001]
		public static SetItemStat Bound(ContainerKind container, uint charInstance, uint slot, uint flags)
		{
			return new SetItemStat(container, charInstance, slot, FlagsStat, flags | BoundFlag);
		}

		// Stat 0 is the entry's `Flags` word; bit 26 is "bound to whoever holds it".
		private const uint FlagsStat = 0;
		private const uint BoundFlag = 1u << 26;
	}
}
