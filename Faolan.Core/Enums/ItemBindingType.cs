namespace Faolan.Core.Enums
{
	// Whether an item INSTANCE is bound to who holds it, and to what (#750).
	//
	// ⚠️ NOT YET DECODED WELL ENOUGH TO SET. Nothing writes this and nothing sends it - it is modelled because binding
	// is per-INSTANCE state and #750's whole point is that per-instance state now has somewhere to live. Do not read
	// the values as a decoded client enum; the numbering is ours.
	//
	// ## What IS measured (2026-08-09, over the corpus' 8,479 snapshot entries)
	//
	// The entry's `stat_list` carries an unnamed **Flags** word at stat id 0 (8,046 sightings, 19 distinct values), and
	// three of its bits behave exactly the way a binding flag would:
	//
	//   bit 24 (0x01000000)   13 templates,  2 varying within a template
	//   bit 26 (0x04000000)  120 templates,  3 varying
	//   bit 27 (0x08000000)   42 templates,  1 varying
	//
	// "Varying within a template" is the interesting part: two copies of ONE template disagree, so the bit is
	// per-instance rather than a property of the item definition. And it is STICKY - it does not follow the container:
	//
	//   "Drinking Cape" (318951)   AssassinL23: EQUIPPED, bit 26 CLEAR
	//                              Hakotep: EQUIPPED, bit 26 SET
	//                              NecroL80:  in her BAG, bit 26 SET
	//
	// ⚠️ That last row is what rules out the easy readings. It is NOT "is equipped" (same container, different values;
	// and set on a bag item). It is also NOT plain bind-on-equip: AssassinL23 has the cape EQUIPPED with the bit CLEAR, and
	// a bind-on-equip flag would have been set the moment it went on.
	//
	// ## ⭐ Bind-on-EQUIP is RULED OUT; the flag is present at PICKUP
	//
	// Measured over the 178 live `DispatchAddItem` entries in the dumps - the moment of ACQUISITION, which no snapshot
	// can show:
	//
	//   103 of 178 arrive with flags 0x00000001   (bits 26/27 CLEAR)
	//    56 of 178 carry bit 26,  27 carry bit 27
	//
	// The decisive one is the "Drinking Cape" (318951), the same template that varies across characters above: it
	// arrives via a CLAIM redemption already carrying **0x0C000001 - bits 26 AND 27 set at delivery**. So the flag is
	// stamped at pickup, and equipping does not stamp it (AssassinL23's copy is worn with the bits clear while Hakotep's
	// worn copy of the same template has them set). If this is a binding, it BINDS ON PICKUP.
	//
	// The value is also CONSISTENT PER TEMPLATE at acquisition: across all 178 adds, zero templates ever arrive with
	// two different flag values. And it is not simply "starter gear" - within the create captures 33 adds are unbound
	// and 31 carry bit 26, split cleanly by template (Slave Manacles unbound; 324066/324067/324139/... bound). That
	// reads as a per-TEMPLATE declaration ("this item is no-trade") stamped onto the instance when it is handed over.
	//
	// ## ⚠️ The one thing that reading does not explain
	//
	// A per-template declaration should never differ between two copies - and for a handful it does (3 templates for
	// bit 26, 1 for bit 27, 2 for bit 24, listed above). Those exceptions are what is left to chase, and they are the
	// reason nothing sets this column yet: the rule is nearly pinned, and "nearly" is not enough to start taking items
	// off players. Candidates: those instances were acquired by a path the corpus does not contain, or predate a
	// server-side change. Tracked in Gitea #756.
	//
	public enum ItemBindingType
	{
		// Freely tradeable. The default, and what every item we mint is today.
		None = 0,

		// Bound to the ACCOUNT: usable by any character on it, but cannot leave it.
		Account = 1,

		// Bound to the one CHARACTER holding it.
		Character = 2
	}
}
