using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Faolan.Core.Network.Server.ChatServer
{
	/// <summary>
	///     One message on the Claude chat channel. <see cref="Sequence" /> is monotonic, for cursor-style polling.
	///     <see cref="ViaWhisper" /> records the inbound MODE: true if a player whispered Claude privately, false if
	///     they posted on the channel - the bridge surfaces it so Claude replies in the SAME mode it was addressed in
	///     (whisper-back vs channel-broadcast). For Claude's own posts (<see cref="FromClaude" />), it mirrors how the
	///     reply was sent.
	/// </summary>
	public sealed record ClaudeChannelMessage(ulong Sequence, DateTime TimestampUtc, uint SenderId, string SenderName, string Message, bool FromClaude, bool ViaWhisper = false);

	/// <summary>
	///     The "Claude" chat channel: a server-defined channel every client is subscribed to on ChatServer login,
	///     bridged to Claude through the MCP server. Player posts are recorded here (the ChatServer listener appends
	///     them as they arrive); Claude reads the log and posts replies via the MCP chat tools, which broadcast
	///     in-game under the synthetic <see cref="SenderId" /> identity. Purely in-memory - the log does not persist
	///     across server restarts.
	/// </summary>
	public sealed class ClaudeChannel
	{
		// The channel's NAME and ID. Its wire identity as a whole (type/name/flag) is declared with every other
		// channel in ChatChannels.Assistant - this half stays here because it is what the LOG is keyed by, and because
		// the name doubles as the sender's.
		public const string ChannelName = "Claude";
		public const string SenderName = "Claude";
		public const uint ChannelId = 0x00C1A0DE;

		/// <summary>Synthetic sender for Claude's posts; must never collide with a character id / clientInstance.</summary>
		public const uint SenderId = 0xC1A0DE05;

		private const int MaxMessages = 500;

		private readonly Lock _lock = new Lock();
		private readonly Queue<ClaudeChannelMessage> _messages = new Queue<ClaudeChannelMessage>();
		private ulong _nextSequence = 1;

		public ulong LatestSequence
		{
			get
			{
				lock (_lock)
				{
					return _nextSequence - 1;
				}
			}
		}

		public ClaudeChannelMessage Append(uint senderId, string senderName, string message, bool fromClaude, bool viaWhisper = false)
		{
			lock (_lock)
			{
				var entry = new ClaudeChannelMessage(_nextSequence++, DateTime.UtcNow, senderId, senderName, message, fromClaude, viaWhisper);
				_messages.Enqueue(entry);
				while (_messages.Count > MaxMessages)
					_messages.Dequeue();

				return entry;
			}
		}

		/// <summary>Messages with Sequence &gt; <paramref name="afterSequence" />, oldest first (0 = everything retained).</summary>
		public IReadOnlyList<ClaudeChannelMessage> ReadSince(ulong afterSequence)
		{
			lock (_lock)
			{
				return _messages.Where(m => m.Sequence > afterSequence).ToArray();
			}
		}
	}
}
