namespace Faolan.Core.Enums
{
	// The client's wire sex code, uniform for players AND NPC creatures: NONE=0 (genderless creatures), MALE=2,
	// FEMALE=3. The character editor sends it doubled (ChangeSex 4/6), zigzag-decoded to 2/3 on read; the spawn IIR
	// + char-list carry it as-is. Players are only ever Male/Female (their CHECK now also permits None, harmlessly).
	public enum Sex : byte
	{
		None = 0,
		Male = 2,
		Female = 3
	}
}
