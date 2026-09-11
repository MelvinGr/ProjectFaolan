namespace Faolan.Core.Network.Server.ChatServer
{
	/// <summary>
	///     The identity of the server-defined "Claude" chat channel: a channel every client is subscribed to on
	///     ChatServer login, which the MCP tools broadcast onto as the synthetic <see cref="SenderId" />.
	///     <para>
	///         Outbound only. There is deliberately no ingestion side: a player's post on this channel is relayed and
	///         persisted like any other channel's and reaches Claude nowhere. What lives here is the wire identity and
	///         nothing else.
	///     </para>
	/// </summary>
	public static class ClaudeChannel
	{
		// The channel's name and id. Its wire identity as a whole (type/name/flag) is declared with every other channel
		// in ChatChannels.Assistant; this half lives here because the name doubles as the sender's.
		public const string ChannelName = "Claude";
		public const string SenderName = "Claude";
		public const uint ChannelId = 0x00C1A0DE;

		/// <summary>Synthetic sender for Claude's posts; must never collide with a character id or clientInstance.</summary>
		public const uint SenderId = 0xC1A0DE05;
	}
}
