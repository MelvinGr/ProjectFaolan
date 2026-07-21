using System;
using System.Collections.Generic;
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
		// The ToggleVanityArmorFilter 'slot' arg / bitmask bit that means the MAIN ("global" hide-all) toggle - slot 31,
		// capture-confirmed (the "toggle vanity global" toggle carries slot 31).
		public const int VanityFilterMainSlot = 31;

		// The equipped BAG (equip slot 3, was mislabeled "Tokens"): its Item.BagExtraSlots sizes the backpack.
		public uint? BagId { get; set; }

		[ForeignKey(nameof(BagId))]
		public virtual Item? Bag { get; set; }

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

		// The equipped RESOURCE bag (equip slot 26): its Item.ResourceBagExtraSlots sizes the resource container, the
		// sibling of Bag for the backpack.
		public uint? ResourceBagId { get; set; }

		[ForeignKey(nameof(ResourceBagId))]
		public virtual Item? ResourceBag { get; set; }

		public uint? RingLeftId { get; set; }

		[ForeignKey(nameof(RingLeftId))]
		public virtual Item? RingLeft { get; set; }

		public uint? RingRightId { get; set; }

		[ForeignKey(nameof(RingRightId))]
		public virtual Item? RingRight { get; set; }

		public uint? ShoulderId { get; set; }

		[ForeignKey(nameof(ShoulderId))]
		public virtual Item? Shoulder { get; set; }

		public uint? VanityBeltId { get; set; }

		[ForeignKey(nameof(VanityBeltId))]
		public virtual Item? VanityBelt { get; set; }

		public uint? VanityBootsId { get; set; }

		[ForeignKey(nameof(VanityBootsId))]
		public virtual Item? VanityBoots { get; set; }

		public uint? VanityCapeId { get; set; }

		[ForeignKey(nameof(VanityCapeId))]
		public virtual Item? VanityCape { get; set; }

		public uint? VanityChestId { get; set; }

		[ForeignKey(nameof(VanityChestId))]
		public virtual Item? VanityChest { get; set; }

		// Vanity ("costume") slots - override the worn item's APPEARANCE only. Retail exposes them at slot ids 1000 +
		// the base slot (VanityHead 1002, VanityChest 1005, VanityBelt 1007, VanityCape 1011, VanityBoots 1014,
		// VanityLegs 1016, VanityWrist 1018).
		public uint? VanityHeadId { get; set; }

		[ForeignKey(nameof(VanityHeadId))]
		public virtual Item? VanityHead { get; set; }

		public uint? VanityLegsId { get; set; }

		[ForeignKey(nameof(VanityLegsId))]
		public virtual Item? VanityLegs { get; set; }

		public uint? VanityWristId { get; set; }

		[ForeignKey(nameof(VanityWristId))]
		public virtual Item? VanityWrist { get; set; }

		public uint? WristId { get; set; }

		[ForeignKey(nameof(WristId))]
		public virtual Item? Wrist { get; set; }

		// The template ids of the STAT-BEARING worn items - the 16 combat slots (armour + jewellery + weapons), reading
		// the *Id columns directly so no navigation load is needed. Excludes the cosmetic Vanity* slots (appearance
		// only) and the Bag / ResourceBag / FirstPosBag containers (their capacity is a bag stat, not a combat stat). A
		// slot occupied by the same template as another (e.g. two identical rings) yields the id twice, so each worn
		// copy counts once when its stat modifiers are aggregated (#3). 0/null slots are skipped.
		public IEnumerable<uint> StatBearingItemIds()
		{
			uint?[] slots =
			[
				NeckId, HeadId, ChestId, MainHandLeftId, BeltId, OffHandLeftId, RingLeftId, RingRightId,
				CloakId, ShoulderId, BootsId, HandsId, LegsId, WristId, MainHandRightId, OffHandRightId
			];
			foreach (var id in slots)
			{
				if (id is > 0)
					yield return id.Value;
			}
		}

		// Indexed access by wire slot id (equipment[CharacterEquipmentSlot.Head]). Every CharacterEquipmentSlot maps to
		// a column - the 17 worn slots plus the 5 vanity slots - so a defined enum value always resolves; the default
		// only guards a raw cast of an out-of-range wire Pos (the bag id, or the enum's gaps), which callers filter with
		// Enum.IsDefined. Get returns the slot's item (null when empty).
		public Item? this[CharacterEquipmentSlot slot]
		{
			get => slot switch
			{
				CharacterEquipmentSlot.Neck          => Neck,
				CharacterEquipmentSlot.Head          => Head,
				CharacterEquipmentSlot.Bag           => Bag,
				CharacterEquipmentSlot.ResourceBag   => ResourceBag,
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
				CharacterEquipmentSlot.VanityChest   => VanityChest,
				CharacterEquipmentSlot.VanityBelt    => VanityBelt,
				CharacterEquipmentSlot.VanityCape    => VanityCape,
				CharacterEquipmentSlot.VanityBoots   => VanityBoots,
				CharacterEquipmentSlot.VanityLegs    => VanityLegs,
				CharacterEquipmentSlot.VanityWrist   => VanityWrist,
				_                                    => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Not a valid equipment slot")
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

					case CharacterEquipmentSlot.Bag:
					{
						Bag = value;
						break;
					}

					case CharacterEquipmentSlot.ResourceBag:
					{
						ResourceBag = value;
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

					case CharacterEquipmentSlot.VanityChest:
					{
						VanityChest = value;
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

					case CharacterEquipmentSlot.VanityLegs:
					{
						VanityLegs = value;
						break;
					}

					case CharacterEquipmentSlot.VanityWrist:
					{
						VanityWrist = value;
						break;
					}

					default:
					{
						throw new ArgumentOutOfRangeException(nameof(slot), slot, "Not a valid equipment slot");
					}
				}
			}
		}

		// --- vanity ("costume") armour filter (client method ToggleVanityArmorFilter, GameCharAgentProject 0x200B).
		// The per-slot toggles are stored as ONE bitmask: bit N SET = slot N's vanity piece is SHOWN. The bit index IS
		// the toggle's wire `slot` arg = the BASE equip slot id, so any slot round-trips by construction (incl. the
		// still-unidentified 29/30 and the 31 master). Emitted VERBATIM to the client on world entry - the FA015807
		// self-spawn's PlayerSpawnScaffold PreFrame carries it as 4 bytes big-endian - so the setting survives relog
		// (the client renders the show/hide off it).
		//
		// ⚠️ POLARITY: 1 = enabled = SHOWN. This was modelled backwards and called VanityHiddenMask, which read as the
		// exact opposite of what it holds, and every comment describing it inherited the inversion. The code was
		// always right (`if (toggleState) mask |= bit`, and the wire's toggleState 1 = enabled); only the NAME and the
		// prose were wrong. NecroL80's captured mask 0x000450A4 is bits 2/5/7/12/14/18 = her Head/Belt/Boots/Wrist
		// vanity SHOWN, which is what the client actually renders - under the old "hidden" reading four of her five
		// vanity pieces would have been hidden, which they visibly are not.
		//
		// Capture-confirmed slot mapping: the vanity-head login dump carries bit 2 (Head), vanity-chest carries bit 5
		// (Chest); the "global" master toggle is slot 31 -> bit 31. See PlayerSpawnScaffoldRows (#336).
		public uint VanityShownMask { get; set; }

		// Apply one ToggleVanityArmorFilter(slot, toggleState): set/clear the slot's bit. toggleState 1 = enabled =
		// SHOWN, so it sets the bit. The wire `slot` is the bit index directly (0..31) - no per-slot mapping needed.
		// Returns false for an out-of-range slot.
		public bool SetVanityFilter(uint slot, bool toggleState)
		{
			if (slot > 31)
				return false;

			var bit = 1u << (int)slot;
			if (toggleState)
				VanityShownMask |= bit;
			else
				VanityShownMask &= ~bit;

			return true;
		}
	}
}
