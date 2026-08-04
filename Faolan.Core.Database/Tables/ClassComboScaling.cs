using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The per-class/level combo scaling curve, lifted whole from the client's own `RDBComboLookup_c` data table
	// (RDB type 1000513, 960 rows = 12 classes x 80 levels). This is a COMBO table, not a stat table: the client
	// looks a row up by (class, level) every time it needs a combo's damage or its stamina cost, and logs
	// "GameCode/Combobook: MISSING combo lookup data for class %u, level %u." when the row is absent.
	//
	// WHAT THE CLIENT DOES WITH IT (FUN_007d7cc0 / FUN_007d7ba0 / FUN_007d7dd0, docs/rdb/client-data-tables.md):
	//   key      = (stat 0x43 = class, min(comboLevel, stat 0x3C5 ApprenticeLevel))
	//   dps      = DpsModifier/100 * BCTCombo::GetComboAnimationLength(combo) * 0.01
	//   stamina  = (int)(MaxStamina(0x1F9) * comboStaminaFactor * animLength / StaminaDivisor)
	// and the combo is startable only while that stamina cost is <= current stamina (0x1F8).
	//
	// ⚠️ THE TWO HALVES ARE NOT EQUALLY LOAD-BEARING. The DPS chain (FUN_007d7cc0) has exactly ONE caller, and it is
	// the combo-book DESCRIPTION builder (FUN_007d6c10 -> FUN_005efbc0, the `ComboStarter`/`ComboFinisher` lines,
	// reached from the combo list and `GetComboTooltip`) - so `DpsModifier` is a number the client SHOWS, never one it
	// spends on a hit. The stamina chain (FUN_007d7ba0) has seven callers including the start gate. Combo damage is
	// the SERVER's, and ours is the combo's own spell (`Combo.GameActionId`) - nothing scales a hit by this column.
	//
	// The client divides the two modifier columns by 100 the moment it parses them; they are carried RAW here (the
	// same convention as ItemStatModifier.Amount / ClassAttributeBase.Amount), so a consumer applies /100 itself.
	[PrimaryKey(nameof(Class), nameof(Level))]
	public class ClassComboScaling
	{
		[ConstrainToEnumValues]
		public ConanClass Class { get; set; }

		// 1..80. Every class carries all 80 rows, including the four that carry no values at all.
		public uint Level { get; set; }

		// The client's own name for it ("MISSING DPS modifier lookup duration for combo"): the class/level DPS a
		// combo's damage is scaled from, at 1/100 units. ZERO for the whole 80 levels of the Priest of Mitra, the
		// Tempest of Set, the Necromancer and the Demonologist - those four classes have no combo scaling data at
		// all and always take the client's "MISSING DPS modifier" fallback of 1.0.
		public uint DpsModifier { get; set; }

		// The second 1/100 column. The client PARSES it (same /100 as DpsModifier) and caches it beside the other
		// two, but on this build nothing reads it back - no code path touches the cached word. Non-zero only for the
		// three soldier classes, and exactly linear in level there: Guardian 500/level, Conqueror and Dark Templar
		// 475/level, so it hits 40000/38000 at level 80. Carried because it is real shipped data, not because its
		// meaning is known.
		public uint SecondaryModifier { get; set; }

		// The DIVISOR in the stamina-cost formula above, so a larger value is a CHEAPER combo. An integer, not a
		// 1/100 value - the client uses it as read. 32 at levels 1-9 stepping down one per decade to 24 at level 80
		// for the eight combo classes; a flat 20 for the four that carry no DPS modifier.
		public uint StaminaDivisor { get; set; }
	}
}
