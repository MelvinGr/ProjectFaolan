using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A character's worn equipment: one item per body/hand slot (null = empty slot). Each slot is an explicit FK id
	// (<Slot>Id, into the Items catalog) alongside its navigation - so a slot's item can be read/set by id without
	// loading the nav (mirrors CharacterInventory.ItemId). The FullChar spawn + appearance are built from these worn
	// items; a character's NON-worn contents live in CharacterInventory. One-to-one with Character, keyed on CharacterId.
	[PrimaryKey(nameof(CharacterId))]
	public class CharacterEquipment
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		public uint CharacterId { get; set; }

		public uint? BeltId { get; set; }
		public virtual Item? Belt { get; set; }

		public uint? BootsId { get; set; }
		public virtual Item? Boots { get; set; }

		public uint? ChestId { get; set; }
		public virtual Item? Chest { get; set; }

		public uint? CloakId { get; set; }
		public virtual Item? Cloak { get; set; }

		public uint? FirstPosBagId { get; set; }
		public virtual Item? FirstPosBag { get; set; }

		public uint? HandsId { get; set; }
		public virtual Item? Hands { get; set; }

		public uint? HeadId { get; set; }
		public virtual Item? Head { get; set; }

		public uint? LegsId { get; set; }
		public virtual Item? Legs { get; set; }

		public uint? MainHandLeftId { get; set; }
		public virtual Item? MainHandLeft { get; set; }

		public uint? MainHandRightId { get; set; }
		public virtual Item? MainHandRight { get; set; }

		public uint? NeckId { get; set; }
		public virtual Item? Neck { get; set; }

		public uint? OffHandLeftId { get; set; }
		public virtual Item? OffHandLeft { get; set; }

		public uint? OffHandRightId { get; set; }
		public virtual Item? OffHandRight { get; set; }

		public uint? RingLeftId { get; set; }
		public virtual Item? RingLeft { get; set; }

		public uint? RingRightId { get; set; }
		public virtual Item? RingRight { get; set; }

		public uint? ShoulderId { get; set; }
		public virtual Item? Shoulder { get; set; }

		public uint? TokensId { get; set; }
		public virtual Item? Tokens { get; set; }

		public uint? WristId { get; set; }
		public virtual Item? Wrist { get; set; }
	}
}
