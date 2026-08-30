namespace Faolan.Core.Network.Server.ChatServer
{
	/// <summary>
	///     The identity of the server-defined "Claude" chat channel: a channel every client is subscribed to on
	///     ChatServer login, which the MCP tools broadcast onto as the synthetic <see cref="SenderId" />.
	///     <para>
	///         OUTBOUND ONLY. There is deliberately no ingestion side: a player's post on this channel is relayed and
	///         persisted like any other channel's and reaches Claude nowhere. The in-memory log that used to record
	///         posts here (and the <c>read_claude_channel</c> tool over it) was removed on 2026-08-30 along with the
	///         stdio channel bridge that drained it - so what is left is the wire identity and nothing else.
	///     </para>
	/// </summary>
	public static class ClaudeChannel
	{
		// The channel's NAME and ID. Its wire identity as a whole (type/name/flag) is declared with every other
		// channel in ChatChannels.Assistant - this half stays here because the name doubles as the sender's.
		public const string ChannelName = "Claude";
		public const string SenderName = "Claude";
		public const uint ChannelId = 0x00C1A0DE;

		/// <summary>Synthetic sender for Claude's posts; must never collide with a character id / clientInstance.</summary>
		public const uint SenderId = 0xC1A0DE05;
	}
}
