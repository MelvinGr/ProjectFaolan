using System;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
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

		public virtual Item? Belt { get; set; }
		public virtual Item? Boots { get; set; }
		public virtual Item? Chest { get; set; }
		public virtual Item? Cloak { get; set; }
		public virtual Item? FirstPosBag { get; set; }
		public virtual Item? Hands { get; set; }
		public virtual Item? Head { get; set; }
		public virtual Item? Legs { get; set; }
		public virtual Item? MainHandLeft { get; set; }
		public virtual Item? MainHandRight { get; set; }
		public virtual Item? Neck { get; set; }
		public virtual Item? OffHandLeft { get; set; }
		public virtual Item? OffHandRight { get; set; }
		public virtual Item? RingLeft { get; set; }
		public virtual Item? RingRight { get; set; }
		public virtual Item? Shoulder { get; set; }
		public virtual Item? Tokens { get; set; }
		public virtual Item? VanityBelt { get; set; }
		public virtual Item? VanityBoots { get; set; }
		public virtual Item? VanityCape { get; set; }
		public virtual Item? VanityHead { get; set; }
		public virtual Item? VanityWrist { get; set; }
		public virtual Item? Wrist { get; set; }

		// Indexed access by wire slot id (equipment[CharacterEquipmentSlot.Head]). Every CharacterEquipmentSlot maps to
		// a column - the 17 worn slots plus the 5 vanity slots - so a defined enum value always resolves; the default
		// only guards a raw cast of an out-of-range wire Pos (the bag id, or the enum's gaps), which callers filter with
		// Enum.IsDefined. Get returns the slot's item (null when empty).
		public Item? this[CharacterEquipmentSlot slot]
		{
			get =>
				slot switch
				{
					CharacterEquipmentSlot.Neck          => Neck,
					CharacterEquipmentSlot.Head          => Head,
					CharacterEquipmentSlot.Tokens        => Tokens,
					CharacterEquipmentSlot.Chest         => Chest,
					CharacterEquipmentSlot.MainWeapon    => MainHandLeft,
					CharacterEquipmentSlot.Belt          => Belt,
					CharacterEquipmentSlot.OffHandWeapon => OffHandLeft,
					CharacterEquipmentSlot.LeftRing      => RingLeft,
					CharacterEquipmentSlot.RightRing     => RingRight,
					CharacterEquipmentSlot.Cape          => Cloak,
					CharacterEquipmentSlot.Shoulder      => Shoulder,
					CharacterEquipmentSlot.Boots         => Boots,
					CharacterEquipmentSlot.Hands         => Hands,
					CharacterEquipmentSlot.Legs          => Legs,
					CharacterEquipmentSlot.Wrist         => Wrist,
					CharacterEquipmentSlot.RangedWeapon  => MainHandRight,
					CharacterEquipmentSlot.Ammo          => OffHandRight,
					CharacterEquipmentSlot.VanityHead    => VanityHead,
					CharacterEquipmentSlot.VanityBelt    => VanityBelt,
					CharacterEquipmentSlot.VanityCape    => VanityCape,
					CharacterEquipmentSlot.VanityBoots   => VanityBoots,
					CharacterEquipmentSlot.VanityWrist   => VanityWrist,
					_                                    => null
				};
			set
			{
				switch (slot)
				{
					case CharacterEquipmentSlot.Neck:
						Neck = value;
						break;
					case CharacterEquipmentSlot.Head:
						Head = value;
						break;
					case CharacterEquipmentSlot.Tokens:
						Tokens = value;
						break;
					case CharacterEquipmentSlot.Chest:
						Chest = value;
						break;
					case CharacterEquipmentSlot.MainWeapon:
						MainHandLeft = value;
						break;
					case CharacterEquipmentSlot.Belt:
						Belt = value;
						break;
					case CharacterEquipmentSlot.OffHandWeapon:
						OffHandLeft = value;
						break;
					case CharacterEquipmentSlot.LeftRing:
						RingLeft = value;
						break;
					case CharacterEquipmentSlot.RightRing:
						RingRight = value;
						break;
					case CharacterEquipmentSlot.Cape:
						Cloak = value;
						break;
					case CharacterEquipmentSlot.Shoulder:
						Shoulder = value;
						break;
					case CharacterEquipmentSlot.Boots:
						Boots = value;
						break;
					case CharacterEquipmentSlot.Hands:
						Hands = value;
						break;
					case CharacterEquipmentSlot.Legs:
						Legs = value;
						break;
					case CharacterEquipmentSlot.Wrist:
						Wrist = value;
						break;
					case CharacterEquipmentSlot.RangedWeapon:
						MainHandRight = value;
						break;
					case CharacterEquipmentSlot.Ammo:
						OffHandRight = value;
						break;
					case CharacterEquipmentSlot.VanityHead:
						VanityHead = value;
						break;
					case CharacterEquipmentSlot.VanityBelt:
						VanityBelt = value;
						break;
					case CharacterEquipmentSlot.VanityCape:
						VanityCape = value;
						break;
					case CharacterEquipmentSlot.VanityBoots:
						VanityBoots = value;
						break;
					case CharacterEquipmentSlot.VanityWrist:
						VanityWrist = value;
						break;
					default:
						throw new ArgumentOutOfRangeException(nameof(slot), slot, "not a valid equipment slot");
				}
			}
		}

		public uint CharacterId { get; set; }

		public uint? BeltId { get; set; }

		public uint? BootsId { get; set; }

		public uint? ChestId { get; set; }

		public uint? CloakId { get; set; }

		public uint? FirstPosBagId { get; set; }

		public uint? HandsId { get; set; }

		public uint? HeadId { get; set; }

		public uint? LegsId { get; set; }

		public uint? MainHandLeftId { get; set; }

		public uint? MainHandRightId { get; set; }

		public uint? NeckId { get; set; }

		public uint? OffHandLeftId { get; set; }

		public uint? OffHandRightId { get; set; }

		public uint? RingLeftId { get; set; }

		public uint? RingRightId { get; set; }

		public uint? ShoulderId { get; set; }

		public uint? TokensId { get; set; }

		public uint? VanityBeltId { get; set; }

		public uint? VanityBootsId { get; set; }

		public uint? VanityCapeId { get; set; }

		// Vanity ("costume") slots - override the worn item's APPEARANCE only. Retail exposes them at slot ids 1000 +
		// the base slot (VanityHead 1002, VanityBelt 1007, VanityCape 1011, VanityBoots 1014, VanityWrist 1018).
		public uint? VanityHeadId { get; set; }

		public uint? VanityWristId { get; set; }

		public uint? WristId { get; set; }
	}
}
