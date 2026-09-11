namespace Faolan.Core.Enums
{
	/// <summary>
	///     Which of a character's two swappable weapon loadouts is currently drawn.
	/// </summary>
	/// <remarks>
	///     A character equips two weapon sets and toggles between them; each set is just a main plus off hand and can
	///     hold any weapons - the two sets are not inherently melee-versus-ranged (a warrior can run 1h+shield in one
	///     and a 2h in the other). Set 2 is commonly a ranged weapon (bow/crossbow plus ammo), which is why its slots
	///     are labelled RangedWeapon/Ammo, but that is the common case, not the meaning. Only the active set's weapon
	///     stats apply and only its weapons render as drawn.
	///     <para />
	///     Retail toggles with the SwitchWeaponsButton action (0x2E), capture-confirmed as the set switch rather than
	///     a draw/sheathe: a character wearing both sets pressed it three times and the server stamped the
	///     newly-active weapon main slot 6 -> 20 -> 6
	///     (Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed).
	/// </remarks>
	public enum WeaponSet
	{
		Primary = 0, // main_hand_left (slot 6) + off_hand_left (slot 8)
		Secondary = 1 // main_hand_right (slot 20) + off_hand_right (slot 21) - often, but not necessarily, ranged
	}
}
