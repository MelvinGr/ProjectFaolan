namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     The in-game mail wire/DB constants, shared by the tradepost service, the MCP economy tools and the web
	///     mail view. Decoded from the NecroL80 buy_from_autionhouse_get_mail capture
	///     (docs/subsystems/tradepost-protocol.md).
	/// </summary>
	public static class MailConstants
	{
		// The per-attachment trailer's leading word (capture: [0x72][0x08006F31][0]).
		public const uint AttachmentTrailerWord = 0x72;

		// The recipient/sender endpoint class the mail body carries, reused as the per-attachment trailer class.
		// 0x08006F31 is the TradepostInterface object id (constant across captures).
		public const uint EndpointClass = 0x08006F31;

		// Mail expiry offset from the send time (~30 days), matching the captured mail's ts2 - ts1 gap.
		public const uint ExpirySeconds = 2_592_000;

		// Mail footer word: 0x0B while unread; flips to 0x09 once the recipient reads it (MarkMailRead).
		public const uint FooterUnread = 0x0B;
	}
}
