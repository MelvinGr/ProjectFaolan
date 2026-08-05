namespace Faolan.Core.Network.Codecs.Spawn
{
	// The character-create EDITOR scene's session constant.
	//
	// ⚠️ This used to BUILD the scene characters, by parsing each captured record and re-emitting it - the
	// "self-reconstruction" of #364. That is gone: the five pedestals and the live avatar preview are typed data in
	// `CreateEditorScene`, generated from the captured burst and gated byte-exact against it, and
	// `CreateFlowService` builds them through the ordinary `SimpleCharFullUpdateCodec` like any other spawn. What is
	// left is the one value that is neither content nor capture-shaped: the session's playfield token.
	//
	// The name is kept because it is what every reference and doc calls this token's home; there is nothing left to
	// build, and no `Reconstruct` to reach for.
	public static class CreateEditorCharBuilder
	{
		// The create-editor session's playfield instance (the MapChange token), shared by every editor record - the
		// scene characters, the scene props and the avatar's own stat sheet.
		public const uint EditorPlayfieldInstance = 0x00502276;
	}
}
