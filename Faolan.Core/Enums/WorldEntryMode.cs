namespace Faolan.Core.Enums
{
	/// <summary>
	///     The role a GameServer connection plays in the world-entry handshake, decided once from the connection's
	///     shape at <c>GameAgent.Authenticate2</c> (the Hello) and then used to route every later handshake signal
	///     (0x205F, CharInPlayIIR, 0x2020).
	/// </summary>
	/// <remarks>
	///     This is the single source of truth for "which entry path is this"; see
	///     <c>docs/subsystems/world-entry-flow.md</c> for the end-to-end map.
	///     <para />
	///     It is the role at authentication time and is stable for the whole connection. It is not the same question
	///     as "is the character named right now" (<c>INetworkClientSession.IsUnnamedCreatePhase</c>): on a
	///     <see cref="CreateEditor" /> connection the character is named mid-connection (CommitCharacterName), so
	///     <c>IsUnnamedCreatePhase</c> flips from true to false while the mode stays <see cref="CreateEditor" />.
	///     Route on the mode when you mean the connection's role; route on <c>IsUnnamedCreatePhase</c> when you mean
	///     the transient pre-name editor phase.
	/// </remarks>
	public enum WorldEntryMode
	{
		/// <summary>
		///     Existing character, normal login. The client drives world entry with a 0x205F then a 0x2020 poll.
		/// </summary>
		Login = 0,

		/// <summary>
		///     A brand-new, not-yet-named character on the character-creator connection. Sends CharInPlayIIR to launch
		///     the editor; carries no world entry until it names and reconnects as <see cref="CreateWorld" />.
		/// </summary>
		CreateEditor,

		/// <summary>
		///     The post-creation world connection (the second connection, opened after the 0x2002 enter-world handoff,
		///     recognised by <c>PendingCreateWorldEntry</c>). Gets the intro/cutscene world entry.
		/// </summary>
		CreateWorld,

		/// <summary>
		///     An existing character reconnecting after a changemap/teleport handoff, recognised by the re-entry mark
		///     <c>TeleportService</c> arms. Behaves like <see cref="Login" /> for the world burst, but the reconnect
		///     carries no client 0x205F, so the post-0x205F follow-up is armed at authentication and flushed on the
		///     CharInPlayIIR or the next 0x2020.
		/// </summary>
		TeleportReentry
	}
}
