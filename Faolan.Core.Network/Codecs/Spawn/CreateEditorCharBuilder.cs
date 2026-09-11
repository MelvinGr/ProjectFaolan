namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     The character-create editor scene's session constant. The scene itself is typed data in CreateEditorScene,
	///     generated from the captured burst and gated byte-exact against it, and CreateFlowService builds its characters
	///     through the ordinary <see cref="SimpleCharFullUpdateCodec" /> like any other spawn; the one value that is neither
	///     content nor capture-shaped is the session's playfield token, which lives here.
	/// </summary>
	public static class CreateEditorCharBuilder
	{
		/// <summary>
		///     The create-editor session's playfield instance - the MapChange token, shared by every editor record: the scene
		///     characters, the scene props and the avatar's own stat sheet.
		/// </summary>
		public const uint EditorPlayfieldInstance = 0x00502276;
	}
}
