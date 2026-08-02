using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs
{
	// The DispatchShortcutBarReply (0x2076) body - the player's saved action bar, sent at login in reply to
	// RequestShortcutBar (0x206D). Decoded 2026-06-19 (#71) from every login dump (47/47; confirmed byte-exact
	// across Strie/Hoxxling/NecroL80):
	//
	//   [u32 (records + 1) * 0x3F1][ N records: [u32 slot][u32 type][u32 id][u32 0][u32 0][u32 0] ]   (24 B/record)
	//
	// The leading word is the ordinary ListFrame count (Strie 15*1009=0x3B1F/14 recs, Hoxxling 23*1009=0x5AA7/22,
	// NecroL80 73*1009=0x11FB9/72). slot = bar position (page 1 = 0..12, page 2 = +100, special bars at 20000+).
	// type = shortcut kind (4 = ability/spell/feat, 2 = item). id = the ability/item id. The c2s side (AddShortcut
	// 0x206E / UpdateShortcut 0x206F / RemoveShortcut 0x2071) already persists into CharacterShortcut; this rebuilds
	// the bar from those rows so it survives relog.
	public static class ShortcutBarReplyCodec
	{
		public const ushort Opcode = (ushort)GameCharInterfaceOpcodes.DispatchShortcutBarReply;

			// A type-1 record's tail names WHERE the shortcut points: [u32 0][u32 containerKind][u32 ownerCharacterId].
		// It is not padding - that reading held only because types 2/3/4 leave it zero, and a strict "expect 0" threw
		// on the first real one, which aborted the whole capture import (no hotbar AND no quests for any imported
		// character). Across the corpus all 207 type-1 records agree exactly: word 4 is 0, word 5 is 0x68 = container
		// 104 (the player's own bag), and word 6 is the bar owner's instance - so the tail is DERIVED here rather than
		// stored per row. Types 2/3/4 (4264 records) carry three zeros.
		private const uint InventoryContainerKind = 0x68;

		// The record kind whose tail is populated: a slot pointing into the owner's own inventory (its `Id` is the
		// position there, and every captured one sits in the 0x4Exx special-bar range).
		private const uint InventorySlotType = 1;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ShortcutBarReply value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The player's persisted bar rows. `owner` is the character the bar belongs to - a type-1 record's tail names
		// it on the wire, and it is not stored per row because it is the same character for every row.
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

	// The saved action bar, plus the character it belongs to (a type-1 record's tail names the owner on the wire).
	public sealed record ShortcutBarReply(uint Owner, IReadOnlyList<ShortcutEntry> Entries);

	// One 24-byte bar record: [u32 slot][u32 type][u32 id][u32 0 x3].
	public readonly record struct ShortcutEntry(uint Slot, uint Type, uint Id);
}
