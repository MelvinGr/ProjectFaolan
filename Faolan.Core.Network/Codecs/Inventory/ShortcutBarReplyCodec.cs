using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Inventory
{
	/// <summary>
	///     The <c>DispatchShortcutBarReply</c> body - the player's saved action bar, sent at login in reply to
	///     <c>RequestShortcutBar</c>.
	/// </summary>
	/// <remarks>
	///     The layout, decoded from every login dump (47 of 47, byte-exact across Strie, Hoxxling and NecroL80):
	///     <code>
	///     [list frame][ N records: [u32 slot][u32 type][u32 id][u32 0][u32 0][u32 0] ]   (24 bytes per record)
	///     </code>
	///     <para>
	///         The leading word is the ordinary <c>ListFrame</c> count. <c>slot</c> is the bar position (page 1 is
	///         0..12, page 2 is +100, special bars sit at 20000 and above). <c>type</c> is the shortcut kind: 4 for an
	///         ability, spell or feat, 3 for a combo, 2 for an item, 1 for a slot in the owner's own bag. <c>id</c> is
	///         the ability/combo/item id, and for type 1 it is the position in that bag.
	///     </para>
	///     <para>
	///         The c2s side (AddShortcut / UpdateShortcut / RemoveShortcut) persists into <c>CharacterShortcut</c>;
	///         this rebuilds the bar from those rows so it survives a relog.
	///     </para>
	/// </remarks>
	public static class ShortcutBarReplyCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchShortcutBarReply;

		// A type-1 record's tail names where the shortcut points: [u32 0][u32 containerKind][u32 ownerCharacterId]. It
		// is not padding, and reading it as such holds only because types 2/3/4 leave it zero. Across the corpus all
		// 207 type-1 records agree exactly: word 4 is 0, word 5 is 0x68 (container 104, the player's own bag) and
		// word 6 is the bar owner's instance, so the tail is derived here rather than stored per row. The 4264
		// records of types 2/3/4 carry three zeros.
		private const uint InventoryContainerKind = 0x68;

		// The record kind whose tail is populated: a slot pointing into the owner's own inventory (its `Id` is the
		// position there, and every captured one sits in the 0x4Exx special-bar range).
		private const uint InventorySlotType = 1;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(ShortcutBarReply value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The player's persisted bar rows. `owner` is the character the bar belongs to: a type-1 record's tail names it
		// on the wire, and it is not stored per row because it is the same character for every row.
		public static byte[] Build(uint owner, IReadOnlyList<CharacterShortcut> shortcuts)
		{
			return Build(new ShortcutBarReply(owner, [.. shortcuts.Select(sc => new ShortcutEntry(sc.Slot, sc.ShortcutType, sc.TargetId))]));
		}

		public static ShortcutBarReply Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ShortcutBarReplyCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static ShortcutBarReply Read(ref SpanReader r)
		{
			var count = r.ReadListCount();
			var entries = new List<ShortcutEntry>(count);
			uint owner = 0;
			for (var i = 0; i < count; i++)
			{
				var entry = new ShortcutEntry(r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
				entries.Add(entry);

				r.ExpectU32Be(0, "shortcut record tail word 4");
				if (entry.Type == InventorySlotType)
				{
					r.ExpectU32Be(InventoryContainerKind, "shortcut inventory container kind");
					owner = r.ReadU32Be();
				}
				else
				{
					r.ExpectU32Be(0, "shortcut record tail word 5");
					r.ExpectU32Be(0, "shortcut record tail word 6");
				}
			}

			return new ShortcutBarReply(owner, entries);
		}

		internal static void Write(FaolanStream s, ShortcutBarReply v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(ListFrame.ForCount(v.Entries.Count));
			foreach (var e in v.Entries)
			{
				s.WriteUInt32(e.Slot).WriteUInt32(e.Type).WriteUInt32(e.Id).WriteUInt32(0);
				if (e.Type == InventorySlotType)
					s.WriteUInt32(InventoryContainerKind).WriteUInt32(v.Owner);
				else
					s.WriteUInt32(0).WriteUInt32(0);
			}
		}
	}

	/// <summary>
	///     The saved action bar, plus the character it belongs to - a type-1 record's tail names the owner on the wire.
	/// </summary>
	public sealed record ShortcutBarReply(uint Owner, IReadOnlyList<ShortcutEntry> Entries);

	/// <summary>One 24-byte bar record: <c>[u32 slot][u32 type][u32 id]</c> plus its three tail words.</summary>
	public readonly record struct ShortcutEntry(uint Slot, uint Type, uint Id);
}
