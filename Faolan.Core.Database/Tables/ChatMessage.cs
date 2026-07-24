using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One persisted chat line (#95). The ChatServer records every channel post and whisper here so chat history
	// survives a restart and can be served back (web/MCP log, moderation, a "recent chat" read on rejoin). This is a
	// HISTORY table, not part of the wire protocol - the live relay (BroadcastChannelChat / whisper) is unchanged;
	// persistence is a write-behind side effect. Keyed by the auto-increment Id; queried newest-first by channel.
	[Index(nameof(SentAt)), Index(nameof(ChannelId))]
	public class ChatMessage : BaseEntity
	{
		public uint ChannelId { get; set; }

		// The sender's ClientInstance (== character id for a player; see player_handle_equals_character_id). 0 for a
		// system/synthetic sender.
		public uint SenderId { get; set; }

		// Non-null for a whisper/tell: the recipient character id. Null for a channel post.
		public uint? TargetId { get; set; }

		// When the server received the message (UTC).
		public DateTimeOffset SentAt { get; set; }

		// True if this line was a private tell rather than a channel broadcast.
		public bool IsWhisper { get; set; }

		// The chat channel type byte + id the post went to (e.g. global channels, or a ~Playfield channel whose id is
		// a map id). For a WHISPER both are 0 and TargetId carries the recipient.
		public byte ChannelType { get; set; }

		[MaxLength(64)]
		public string SenderName { get; set; } = null!;

		[MaxLength(512)]
		public string Text { get; set; } = null!;
	}
}
