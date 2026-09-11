namespace Faolan.Core.Enums
{
	/// <summary>
	///     The client's wire sex code, uniform for players and NPC creatures: None=0 (genderless creatures), Male=2,
	///     Female=3.
	/// </summary>
	/// <remarks>
	///     The character editor sends it doubled (ChangeSex 4/6), zigzag-decoded to 2/3 on read; the spawn IIR and the
	///     character list carry it as-is. Players are only ever Male or Female, though their check constraint also
	///     permits None.
	/// </remarks>
	public enum Sex : byte
	{
		None = 0,

		/// <summary>
		///     A second non-gendered code the wire also uses.
		/// </summary>
		/// <remarks>
		///     Observed on five types, every one of them genderless: the elemental Runners (Magma, Lava, Mud, Venomous
		///     Mud) and the Wheel of Pain, which is a prop rather than a creature. It is not distinguishable from
		///     <see cref="None" /> on the evidence: 0 is carried by animals, demons and some humanoids (Horse, Cobra,
		///     Crocodile, Fha'quth, Blood Arcanist) that are equally genderless, and no behavioural difference between
		///     the two has been found - most likely two authoring conventions for the same "no gender". The name
		///     therefore describes the observed population, not a proven distinction.
		///     <para />
		///     Declared because the value is real and silence is not an option: <c>NpcStatCore.Sex</c> is
		///     <c>[ConstrainToEnumValues]</c>, so an undeclared code fails the generated check constraint and aborts
		///     an import.
		/// </remarks>
		Neuter = 1,
		Male = 2,
		Female = 3
	}
}
