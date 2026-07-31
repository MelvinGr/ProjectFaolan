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

		// The three words that pad each record out to 24 bytes. Zero in every captured bar.
		private const int PaddingWords = 3;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ShortcutBarReply value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The player's persisted bar rows.
		public static byte[] Build(IReadOnlyList<CharacterShortcut> shortcuts)
		{
			return Build(new ShortcutBarReply([.. shortcuts.Select(sc => new ShortcutEntry(sc.Slot, sc.ShortcutType, sc.TargetId))]));
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
			for (var i = 0; i < count; i++)
			{
				entries.Add(new ShortcutEntry(r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be()));
				for (var w = 0; w < PaddingWords; w++)
					r.ExpectU32Be(0, "shortcut record padding");
			}

			return new ShortcutBarReply(entries);
		}

		internal static void Write(FaolanStream s, ShortcutBarReply v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(ListFrame.ForCount(v.Entries.Count));
			foreach (var e in v.Entries)
			{
				s.WriteUInt32(e.Slot).WriteUInt32(e.Type).WriteUInt32(e.Id);
				for (var w = 0; w < PaddingWords; w++)
					s.WriteUInt32(0);
			}
		}
	}

	// The saved action bar: one entry per occupied slot.
	public sealed record ShortcutBarReply(IReadOnlyList<ShortcutEntry> Entries);

	// One 24-byte bar record: [u32 slot][u32 type][u32 id][u32 0 x3].
	public readonly record struct ShortcutEntry(uint Slot, uint Type, uint Id);
}
