namespace Faolan.Core.Enums
{
	// Which element a `remoteformat` inner value is. These are the only three shapes in the corpus (verified over
	// all 8232 stored values), and they are what the client resolves a slot's text from:
	//   Localized   - a reference into the client's LDB string table, by numeric id or by name token
	//   StringEntry - a literal string the client renders verbatim (the session slots: player name, guild, ...)
	//   RemoteFormat- a nested parameter-less remoteformat root (only world-object display values carry these)
	public enum InnerElementKind : byte
	{
		Localized = 0,
		StringEntry = 1,
		RemoteFormat = 2
	}
}
