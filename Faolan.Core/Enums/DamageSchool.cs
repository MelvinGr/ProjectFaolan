namespace Faolan.Core.Enums
{
	/// <summary>
	///     The DamageResultIIR (0xFEC4158A) <c>type</c> field: the ATTACKER's damage school / weapon type - NOT a hit
	///     result. RE'd across all 105 captures (2026-07-22): it is CONSTANT per attacker (every hit from a given
	///     entity carries the same value), the two trailing wire fields are always 0, and there is no crit/miss flag -
	///     a crit is conveyed only by the larger number, and misses aren't sent as DamageResult at all.
	///     The client (<c>FUN_0149a8f0</c>) treats <c>1..14</c> as valid and renders <c>0</c> or <c>&gt;14</c> as
	///     "GenericDamage". The order is confirmed from the client LDB (TextStrings 1030002: Slashing, Piercing,
	///     Crushing, Fire, Cold, Electrical, Holy, …), matching the InvulnerabilityRating stat order; the tail
	///     (Unholy/Poison/Siege) is from that same order. The player's physical melee is capture-observed as
	///     <see cref="Crushing" /> (3) - weapon-dependent in retail, but we don't model per-weapon schools.
	/// </summary>
	public enum DamageSchool : uint
	{
		Generic = 0,
		Slashing = 1,
		Piercing = 2,
		Crushing = 3,
		Fire = 4,
		Cold = 5,
		Electrical = 6,
		Holy = 7,
		Unholy = 8,
		Poison = 9,
		Siege = 10
	}
}
