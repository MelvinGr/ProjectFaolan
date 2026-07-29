namespace Faolan.Core.Enums
{
	// The client's wire sex code, uniform for players AND NPC creatures: NONE=0 (genderless creatures), MALE=2,
	// FEMALE=3. The character editor sends it doubled (ChangeSex 4/6), zigzag-decoded to 2/3 on read; the spawn IIR
	// + char-list carry it as-is. Players are only ever Male/Female (their CHECK now also permits None, harmlessly).
	public enum Sex : byte
	{
		None = 0,

		// A SECOND non-gendered code the wire also uses. Observed on 5 types, every one of them genderless: the
		// elemental Runners (Magma / Lava / Mud / Venomous Mud) and the Wheel of Pain, which is a prop rather than a
		// creature.
		//
		// NOT distinguishable from None on the evidence: 0 is carried by animals, demons and some humanoids (Horse,
		// Cobra, Crocodile, Fha'quth, Blood Arcanist) that are equally genderless, and no behavioural difference
		// between the two has been found - most likely two authoring conventions for the same "no gender". So the
		// name describes the observed population, not a proven distinction.
		//
		// Declared because the value is REAL and silence is not an option: NpcStatCore.Sex is
		// [ConstrainToEnumValues], so an undeclared code fails the generated CHECK - the first capture containing one
		// aborted the entire import.
		Neuter = 1,
		Male = 2,
		Female = 3
	}
}
