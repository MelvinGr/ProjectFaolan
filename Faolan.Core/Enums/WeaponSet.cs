namespace Faolan.Core.Enums
{
	// Which of a character's two swappable weapon LOADOUTS is currently drawn (#400). A character equips two weapon
	// sets and toggles between them; each set is just a main + off hand and can hold ANY weapons - the two sets are
	// NOT inherently melee-vs-ranged (a warrior can run 1h+shield in one and a 2h in the other). Set 2 is commonly a
	// ranged weapon (bow/crossbow + ammo), which is why its slots were labelled RangedWeapon/Ammo, but that is only
	// the common case, not the meaning. Only the ACTIVE set's weapon stats apply and only its weapons render as drawn.
	//
	// Retail toggles with the SwitchWeaponsButton action (0x2E) - capture-confirmed as the SET switch, not a
	// draw/sheathe: a character wearing both sets pressed it 3x and the server stamped the newly-active weapon main
	// slot 6 -> 20 -> 6 (Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed).
	public enum WeaponSet
	{
		Primary = 0,  // main_hand_left (slot 6) + off_hand_left (slot 8)
		Secondary = 1 // main_hand_right (slot 20) + off_hand_right (slot 21) - often, but not necessarily, ranged
	}
}
