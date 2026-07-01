using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Outbound
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
			using var s = new FaolanStream();
			s.WriteUInt32(((uint)shortcuts.Count + 1) * ListFraming);
			foreach (var sc in shortcuts)
			{
				s.WriteUInt32(sc.Slot);
				s.WriteUInt32(sc.ShortcutType);
				s.WriteUInt32(sc.TargetId);
				s.WriteUInt32(0);
				s.WriteUInt32(0);
				s.WriteUInt32(0);
			}

			return s.ToArray();
		}
	}
}
