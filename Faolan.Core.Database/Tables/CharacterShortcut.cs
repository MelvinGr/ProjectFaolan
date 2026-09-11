using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One hotbar shortcut, as reported by the client's GameCharAgent.AddShortcut/RemoveShortcut
	// ([u32 type][u32 slot][u32 targetId][u32 0] / [u32 slot]). The slot value is the client's own slot id
	// (observed ranges 0x64.. and 0x4E20.. - bar pages); type 4 = ability, 2/3 observed for items/other.
	// Note: persistence only for now - how the bar is restored at login is undecoded (RequestShortcutBar gets
	// no direct reply in retail; the restore most likely rides inside the FullChar 0x737A6DF9 snapshot).
	[PrimaryKey(nameof(CharacterId), nameof(Slot))]
	public class CharacterShortcut
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		public uint TargetId { get; set; }
		public uint ShortcutType { get; set; }
		public uint Slot { get; set; }
	}
}
