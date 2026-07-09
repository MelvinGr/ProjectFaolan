using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs
{
	// Builds the ShortcutBarReply (0x2076 DispatchShortcutBarReply) body - the player's saved action bar, sent at
	// login in reply to RequestShortcutBar (0x206D). Decoded 2026-06-19 (#71) from every login dump (47/47;
	// confirmed byte-exact across Strie/Hoxxling/NecroL80):
	//
	//   [u32 header][ N records: [u32 slot][u32 type][u32 id][u32 0][u32 0][u32 0] ]   (24 B/record)
	//
	// header = (N + 1) * 0x3F1 (the 0x3F1 list-framing invariant: Strie 15*1009=0x3B1F/14 recs,
	// Hoxxling 23*1009=0x5AA7/22, NecroL80 73*1009=0x11FB9/72). slot = bar position (page 1 = 0..12, page 2 = +100,
	// special bars at 20000+). type = shortcut kind (4 = ability/spell/feat, 2 = item). id = the ability/item id.
	// The c2s side (AddShortcut 0x206E / UpdateShortcut 0x206F / RemoveShortcut 0x2071) already persists into
	// CharacterShortcut; this rebuilds the bar from those rows so it survives relog.
	public static class ShortcutBarBuilder
	{
		private const uint ListFraming = 0x3F1;

		public static byte[] BuildBody(IReadOnlyList<CharacterShortcut> shortcuts)
		{
			return new ShortcutBar([.. shortcuts.Select(sc => new ShortcutEntry(sc.Slot, sc.ShortcutType, sc.TargetId))]).ToArray();
		}

		// One 24-byte bar record: [u32 slot][u32 type][u32 id][u32 0 x3].
		public sealed record ShortcutEntry(uint Slot, uint Type, uint Id);

		// The ShortcutBarReply body as a self-serializing codec (ICodec): Write frames the 0x3F1-count header + the
		// per-slot records; Read recovers the entries. BuildBody maps the CharacterShortcut DB rows to it.
		public sealed record ShortcutBar(IReadOnlyList<ShortcutEntry> Entries) : ICodec<ShortcutBar>
		{
			public static ShortcutBar Read(FaolanStream s)
			{
				var header = s.ReadUInt32();
				var count = header / ListFraming - 1;
				var entries = new List<ShortcutEntry>(checked((int)count));
				for (var i = 0u; i < count; i++)
				{
					var slot = s.ReadUInt32();
					var type = s.ReadUInt32();
					var id = s.ReadUInt32();
					s.ReadUInt32();
					s.ReadUInt32();
					s.ReadUInt32();
					entries.Add(new ShortcutEntry(slot, type, id));
				}

				return new ShortcutBar(entries);
			}

			public void Write(FaolanStream s)
			{
				s.WriteUInt32((Entries.Count + 1) * ListFraming);
				foreach (var e in Entries)
				{
					s.WriteUInt32(e.Slot);
					s.WriteUInt32(e.Type);
					s.WriteUInt32(e.Id);
					s.WriteUInt32(0);
					s.WriteUInt32(0);
					s.WriteUInt32(0);
				}
			}
		}
	}
}
