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
		public uint? BeltId { get; set; }

		[ForeignKey(nameof(BeltId))]
		public virtual Item? Belt { get; set; }

		public uint? BootsId { get; set; }

		[ForeignKey(nameof(BootsId))]
		public virtual Item? Boots { get; set; }

		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		public uint? ChestId { get; set; }

		[ForeignKey(nameof(ChestId))]
		public virtual Item? Chest { get; set; }

		public uint? CloakId { get; set; }

		[ForeignKey(nameof(CloakId))]
		public virtual Item? Cloak { get; set; }

		public uint? FirstPosBagId { get; set; }

		[ForeignKey(nameof(FirstPosBagId))]
		public virtual Item? FirstPosBag { get; set; }

		public uint? HandsId { get; set; }

		[ForeignKey(nameof(HandsId))]
		public virtual Item? Hands { get; set; }

		public uint? HeadId { get; set; }

		[ForeignKey(nameof(HeadId))]
		public virtual Item? Head { get; set; }

		public uint? LegsId { get; set; }

		[ForeignKey(nameof(LegsId))]
		public virtual Item? Legs { get; set; }

		public uint? MainHandLeftId { get; set; }

		[ForeignKey(nameof(MainHandLeftId))]
		public virtual Item? MainHandLeft { get; set; }

		public uint? MainHandRightId { get; set; }

		[ForeignKey(nameof(MainHandRightId))]
		public virtual Item? MainHandRight { get; set; }

		public uint? NeckId { get; set; }

		[ForeignKey(nameof(NeckId))]
		public virtual Item? Neck { get; set; }

		public uint? OffHandLeftId { get; set; }

		[ForeignKey(nameof(OffHandLeftId))]
		public virtual Item? OffHandLeft { get; set; }

		public uint? OffHandRightId { get; set; }

		[ForeignKey(nameof(OffHandRightId))]
		public virtual Item? OffHandRight { get; set; }

		public uint? RingLeftId { get; set; }

		[ForeignKey(nameof(RingLeftId))]
		public virtual Item? RingLeft { get; set; }

		public uint? RingRightId { get; set; }

		[ForeignKey(nameof(RingRightId))]
		public virtual Item? RingRight { get; set; }

		public uint? ShoulderId { get; set; }

		[ForeignKey(nameof(ShoulderId))]
		public virtual Item? Shoulder { get; set; }

		public uint? TokensId { get; set; }

		[ForeignKey(nameof(TokensId))]
		public virtual Item? Tokens { get; set; }

		public uint? VanityBeltId { get; set; }

		[ForeignKey(nameof(VanityBeltId))]
		public virtual Item? VanityBelt { get; set; }

		public uint? VanityBootsId { get; set; }

		[ForeignKey(nameof(VanityBootsId))]
		public virtual Item? VanityBoots { get; set; }

		public uint? VanityCapeId { get; set; }

		[ForeignKey(nameof(VanityCapeId))]
		public virtual Item? VanityCape { get; set; }

		// Vanity ("costume") slots - override the worn item's APPEARANCE only. Retail exposes them at slot ids 1000 +
		// the base slot (VanityHead 1002, VanityBelt 1007, VanityCape 1011, VanityBoots 1014, VanityWrist 1018).
		public uint? VanityHeadId { get; set; }

		[ForeignKey(nameof(VanityHeadId))]
		public virtual Item? VanityHead { get; set; }

		public uint? VanityWristId { get; set; }

		[ForeignKey(nameof(VanityWristId))]
		public virtual Item? VanityWrist { get; set; }

		public uint? WristId { get; set; }

		[ForeignKey(nameof(WristId))]
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
					{
						Neck = value;
						break;
					}

					case CharacterEquipmentSlot.Head:
					{
						Head = value;
						break;
					}

					case CharacterEquipmentSlot.Tokens:
					{
						Tokens = value;
						break;
					}

					case CharacterEquipmentSlot.Chest:
					{
						Chest = value;
						break;
					}

					case CharacterEquipmentSlot.MainWeapon:
					{
						MainHandLeft = value;
						break;
					}

					case CharacterEquipmentSlot.Belt:
					{
						Belt = value;
						break;
					}

					case CharacterEquipmentSlot.OffHandWeapon:
					{
						OffHandLeft = value;
						break;
					}

					case CharacterEquipmentSlot.LeftRing:
					{
						RingLeft = value;
						break;
					}

					case CharacterEquipmentSlot.RightRing:
					{
						RingRight = value;
						break;
					}

					case CharacterEquipmentSlot.Cape:
					{
						Cloak = value;
						break;
					}

					case CharacterEquipmentSlot.Shoulder:
					{
						Shoulder = value;
						break;
					}

					case CharacterEquipmentSlot.Boots:
					{
						Boots = value;
						break;
					}

					case CharacterEquipmentSlot.Hands:
					{
						Hands = value;
						break;
					}

					case CharacterEquipmentSlot.Legs:
					{
						Legs = value;
						break;
					}

					case CharacterEquipmentSlot.Wrist:
					{
						Wrist = value;
						break;
					}

					case CharacterEquipmentSlot.RangedWeapon:
					{
						MainHandRight = value;
						break;
					}

					case CharacterEquipmentSlot.Ammo:
					{
						OffHandRight = value;
						break;
					}

					case CharacterEquipmentSlot.VanityHead:
					{
						VanityHead = value;
						break;
					}

					case CharacterEquipmentSlot.VanityBelt:
					{
						VanityBelt = value;
						break;
					}

					case CharacterEquipmentSlot.VanityCape:
					{
						VanityCape = value;
						break;
					}

					case CharacterEquipmentSlot.VanityBoots:
					{
						VanityBoots = value;
						break;
					}

					case CharacterEquipmentSlot.VanityWrist:
					{
						VanityWrist = value;
						break;
					}

					default:
					{
						throw new ArgumentOutOfRangeException(nameof(slot), slot, "not a valid equipment slot");
					}
				}
			}
		}

		// --- vanity ("costume") armour FILTER toggles (client method ToggleVanityArmorFilter, GameCharAgentProject
		// 0x200B). A vanity slot is just its equipment slot + a show/hide toggle: when a slot is filtered the
		// character's vanity item for that slot is withheld from the spawn snapshot so the real worn item shows
		// through. VanityAllHidden is the "main" master toggle (filter every vanity piece at once). Persisted so the
		// choice survives relog - the client renders the show/hide itself and reads the state back from the equipment
		// carried in the character snapshot (PlayerCharacterIir), not a dedicated wire field.
		public bool VanityAllHidden { get; set; }
		public bool VanityHeadHidden { get; set; }
		public bool VanityBeltHidden { get; set; }
		public bool VanityCapeHidden { get; set; }
		public bool VanityBootsHidden { get; set; }
		public bool VanityWristHidden { get; set; }

		// The ToggleVanityArmorFilter 'slot' arg that means the MAIN (hide-all) toggle rather than a single slot.
		// [V0: best guess - the most frequent lone value across captures (2/29/30/31 seen); the per-slot toggles use
		// the BASE equip slot id (slot=2 -> Head is capture-confirmed). Verify the master id + the 30/31 slots + the
		// toggleState polarity in-client, then correct these constants - see Gitea #336.]
		public const uint VanityFilterMainSlot = 29; // TODO: Check me

		// Apply one ToggleVanityArmorFilter(slot, toggleState) to the stored per-slot filter. Maps the wire 'slot' to
		// the matching vanity toggle (main or one of the five modelled vanity slots, keyed by BASE equip slot id).
		// Returns false for a slot we don't model (an appearance slot beyond our five) so the caller can log it.
		public bool SetVanityFilter(uint slot, bool hidden)
		{
			switch (slot)
			{
				case VanityFilterMainSlot: VanityAllHidden = hidden; return true;
				case (uint)CharacterEquipmentSlot.Head: VanityHeadHidden = hidden; return true;
				case (uint)CharacterEquipmentSlot.Belt: VanityBeltHidden = hidden; return true;
				case (uint)CharacterEquipmentSlot.Cape: VanityCapeHidden = hidden; return true;
				case (uint)CharacterEquipmentSlot.Boots: VanityBootsHidden = hidden; return true;
				case (uint)CharacterEquipmentSlot.Wrist: VanityWristHidden = hidden; return true;
				default: return false;
			}
		}

		// Whether the given VANITY slot's item should be WITHHELD from the appearance (its own toggle, or the master,
		// is set). Non-vanity slots are never hidden by this filter.
		public bool IsVanityHidden(CharacterEquipmentSlot slot)
		{
			// The master (VanityAllHidden) only withholds VANITY slots - never the real worn gear, which always ships.
			return slot switch
			{
				CharacterEquipmentSlot.VanityHead => VanityAllHidden || VanityHeadHidden,
				CharacterEquipmentSlot.VanityBelt => VanityAllHidden || VanityBeltHidden,
				CharacterEquipmentSlot.VanityCape => VanityAllHidden || VanityCapeHidden,
				CharacterEquipmentSlot.VanityBoots => VanityAllHidden || VanityBootsHidden,
				CharacterEquipmentSlot.VanityWrist => VanityAllHidden || VanityWristHidden,
				_ => false
			};
		}
	}
}
