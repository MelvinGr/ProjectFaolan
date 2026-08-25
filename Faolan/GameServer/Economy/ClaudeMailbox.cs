using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Server.ChatServer;

namespace Faolan.GameServer.Economy
{
	// The "Claude" mailbox (#281): in-game mail addressed to the Claude chat channel's own name.
	//
	// No Character row stands behind it, and none is needed: Mail keys its recipient by NAME + realm (the only FK is
	// the realm), and the one thing the send path wanted a character for was the "does the recipient exist" check.
	// So the mailbox IS the name. A mail that lands here is surfaced to Claude through the same in-memory log the
	// chat bridge already polls (ClaudeChannel) as a whisper-mode entry - the shape a /tell reaches him in - and the
	// MCP read_claude_mail tool reads the rows back; his answer goes out through send_mail or a whisper.
	public static class ClaudeMailbox
	{
		public const string Name = ClaudeChannel.SenderName;

		public static bool IsClaude(string? recipientName)
		{
			return string.Equals(recipientName?.Trim(), Name, StringComparison.OrdinalIgnoreCase);
		}

		// The channel-log line for one mail: what the bridge hands Claude. Money and the attachment are spelled out so
		// he can answer without a second read; the mail id is what read_claude_mail / mark-read address.
		public static string Describe(Mail mail)
		{
			var text = $"[mail 0x{mail.MailId:X16}] {mail.BodyXml}";
			if (mail.Money > 0)
				text += $" (+{mail.Money} tin attached)";

			var attachment = mail.Attachments.FirstOrDefault();
			if (attachment != null)
				text += $" (+{attachment.Quantity}x item {attachment.Template} attached)";

			return text;
		}
	}
}
