namespace Faolan.Core.Enums
{
	// What a `SimpleDynelVisualChangeIIR` (0xE7F4F8F8) changes about a world object - the `kind` word that selects
	// its payload.
	//
	// These are the client's own names. The applier beside the reader
	// (`SimpleDynelVisualChangeIIR_c::SimpleDynelVisualChangeIIR_c()` = FUN_007a6480) switches on this word and logs
	// "e_<name> Visual change iir is activating" into the `GameCode/simpledynel` debug channel before it acts, so
	// each case carries its enum member as a string; the two that log through a shared helper
	// (`PlayActionAnim`, `SetDisplayName`) are read off the string operand of that call. Nothing here is
	// inferred from what a payload looked like.
	//
	// The payload each one takes is in `SimpleDynelVisualChangeCodec` (sizes) and `SimpleDynelVisualChanges`
	// (meanings). Kinds the corpus never carries are implemented anyway, because the client's reader states their
	// size - an unimplemented one would be a parse failure on a body retail is free to send.
	public enum DynelVisualChange : uint
	{
		SetBaseAnim = 0, // [u16 len][ascii] - the resting animation ("Open", "object_open", "object_closed")
		PlayActionAnim = 1, // [u16 len][ascii] - a one-shot animation
		StartParticleEffect = 2, // [effect id (RDB 1010207)][attractor][rotation deg][x][y][z]
		StopParticleEffect = 3, // [effect id] (0 in every captured record)
		ChangeMesh = 4, // [RDB record id][RDB type] - the geometry pair the spawn carries; id 0 draws nothing
		SetKdTree = 5, // [collision-mesh id] - the mesh manifest's <CollisionMesh2 file>; 0 drops collision
		SetName = 6, // [u16 len][ascii]
		SetDisplayName = 7, // [u16 len][ascii]
		FadeIn = 8, // [f32 seconds]
		FadeOut = 9, // [f32 seconds]
		ChangeDecal = 10, // [decal id]
		SetBounds = 11, // 12 x u32
		SetInverseObbMatrix = 12 // 16 x u32 (a 4x4 matrix)
	}
}
