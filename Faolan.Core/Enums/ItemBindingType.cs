namespace Faolan.Core.Enums
{
	// Whether an item INSTANCE is bound to who holds it, and to what (#750).
	//
	// ⚠️ UNDECODED ON THE WIRE. Nothing sets this yet and nothing sends it - it is modelled because binding is
	// per-INSTANCE state and #750's whole point is that per-instance state now has somewhere to live. Do not read the
	// values as a decoded client enum; the numbering is ours.
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
	// ## Why it cannot be settled from what we have
	//
	// Every corpus sighting of the Flags word is a LOGIN SNAPSHOT, i.e. steady state. Binding is a TRANSITION, so no
	// number of snapshots can distinguish bind-on-pickup from bind-on-equip - the flip is never in frame. Settling it
	// needs a capture that contains the transition: acquire an item, note its Flags in the live `DispatchAddItem`,
	// equip it, and read the flag again. Tracked in Gitea.
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
