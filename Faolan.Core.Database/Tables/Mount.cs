using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// One summonable mount (#27/#627), lifted whole from the client's own catalog: RDB type 1040005 (the spell
	// catalog) field 186 `Mount` - the client's name for stat 0x0BA, the same id the ride buff pushes as the
	// mounted-state stat. It occurs on 155 spell records, and every one of them IS a mount summon.
	//
	// This replaces the two hand-built rows `MountCatalog` used to carry. Those were not wrong - they were the two
	// the mount capture happened to show - but the client ships the whole list, so the catalog was a two-row slice
	// of a table that already exists in the data (construct, don't replay).
	//
	// What stays in MountCatalog is what the RDB does NOT state: the 545 ms summon channel, the ride-buff and
	// dismount spell ids, and the ride-buff payload shape. Those are capture-derived and have no field here.
	public class Mount
	{
		// The mount id the CLIENT sends: the `from` word of its BehaviorControlCenter SummonMount action. It is the
		// summon spell's own record id in the RDB spell catalog - `0x3E34F1` is spell 4076785 "Fading Black Horse" -
		// which is why this is an FK to Spells rather than a free-standing number: the name the mount is summoned
		// with lives on that row, and a mount whose spell is not catalogued is a failure, not an orphan.
		[Key]
		public uint Id { get; set; }

		[ForeignKey(nameof(Id))]
		public virtual Spell Spell { get; set; } = null!;

		// RDB field 186's value: the mount CREATURE id. This is what the server puts in the `Mount` stat
		// (CharacterStatId.Mount, 0x0BA) alongside the ride buff, and it is what the client animates the whole
		// horse-runs-up-and-mount from - retail spawns no server-side horse entity (#27). The mount capture's
		// frame-520 StatUpd carries 0x00313C9B = 3226779, which is exactly field 186 of spell 4076785.
		//
		// NOT unique: "Killer Rhino" and "War Mammoth" each have two summon spells naming one creature, and the
		// three "Ridden Death" spells name three different ones. The mount ID is the key, not the creature.
		public uint CreatureId { get; set; }

		/// <summary>
		///     RDB field 499 of the same record - the QUALITY/rarity tier, the very same field (and the very same
		///     0-indexed <see cref="ItemQuality" /> ladder) that <c>RdbItemImporter</c> reads off an item template.
		///     NULL when the record carries no such field, which is every plain and barded horse.
		/// </summary>
		/// <remarks>
		///     This is what separates a mount's variants from each other, and it is the answer to #627's "a speed
		///     field would let MountSpeed come from data": there is no speed field. Field 499 is the ONLY field that
		///     differs between "Fading Black Horse" (none), "Swift Fading Black Horse" (2) and "Azure-barded Fading
		///     Black Horse" (none) - every other field of all three is identical bar the icon and the creature id.
		///     <para>
		///         It reads as rarity across the whole set and not as speed: the ladder runs NULL for the level-40
		///         starter horses, 2 <c>Enchanted</c> for the Swift horses / Killer Rhino / War Mammoth / Ice Bear,
		///         3 <c>Rare</c> for the war wolves and Hyrkanian mounts, 4 <c>Epic</c> for the Yothian War-Mares and
		///         Vaaghasan Slaughter Steeds, 5 <c>Legendary</c> for Blood of Zath and Abomination of Selkhet. 1
		///         <c>Superior</c> never occurs - the same sparse use of the ladder gear shows.
		///     </para>
		///     <para>
		///         So <c>MountSpeed</c> stays the capture-proven flat 100 (mount capture frame 520). Deriving a speed
		///         from a rarity tier would be an invention, and the one mount a capture pins - the tierless Fading
		///         Black Horse - is already at 100, so there is not even a baseline to scale from.
		///     </para>
		/// </remarks>
		[ConstrainToEnumValues]
		public ItemQuality? Quality { get; set; }
	}
}
