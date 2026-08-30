namespace Faolan.Core.Enums
{
	// The role a GameServer connection plays in the world-entry handshake, decided ONCE from the connection's
	// shape at GameAgent.Authenticate2 (the Hello) and then used to route every later handshake signal
	// (0x205F / CharInPlayIIR / 0x2020). This is the single source of truth for "which entry path is this";
	// see docs/subsystems/world-entry-flow.md for the end-to-end map.
	//
	// NB this is the role AT AUTH TIME and is stable for the whole connection. It is NOT the same question as
	// "is the character named right now" (INetworkClientSession.IsUnnamedCreatePhase): on a CreateEditor
	// connection the character is named mid-connection (CommitCharacterName), so IsUnnamedCreatePhase flips
	// from true to false while the mode stays CreateEditor. Route on the mode when you mean the connection's
	// role; route on IsUnnamedCreatePhase when you mean the transient pre-name editor phase.
	public enum WorldEntryMode
	{
		// Existing character, normal login. The client drives world entry with a 0x205F then a 0x2020 poll.
		Login = 0,

		// A brand-new, not-yet-named character on the character-creator connection. Sends CharInPlayIIR to
		// launch the editor; carries no world entry until it names + reconnects as CreateWorld.
		CreateEditor,

		// The post-creation WORLD connection (the second connection, opened after the 0x2002 enter-world
		// handoff, recognised by PendingCreateWorldEntry). Gets the intro/cutscene world entry.
		CreateWorld,

		// An existing character reconnecting after a changemap/teleport handoff (recognised by the re-entry mark
		// TeleportService arms). Behaves like Login for the world burst, but the reconnect carries NO client
		// 0x205F, so the post-0x205F follow-up is armed at auth and flushed on the CharInPlayIIR / next 0x2020.
		TeleportReentry
	}
}
