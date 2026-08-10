using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// Finds the `itemref://` anchors inside one chat message and reads each with `ItemRefLinkCodec` (#751).
	//
	// A NEIGHBOUR, not a codec: it neither encodes nor decodes a message of its own, it LOCATES the elements of one -
	// the chat-text analogue of picking the `<parameter>` values out of a remoteformat. It lives here rather than in
	// the importer because finding a wire element is decode, and decode lives in `Codecs/`.
	//
	// ⚠️ A chat body is a MIXED buffer: framing words, a sender handle, then Latin1 text. Nothing here assumes the
	// whole thing is a string - it looks for the scheme and takes the anchor around it, so a link is found whichever
	// carrier it rode in on (ChatServer `0x0041` trade chat, ChatServer `0x0022`, or the c2s Conan `0x2002`).
	public static class ChatItemLinks
	{
		// Every item link in `text`. `Rejected` counts anchors that carry the scheme and were NOT readable - reported
		// rather than swallowed, because a chat link is player-typed text and a rise in that count is the signal that
		// either the grammar moved or someone is typing the scheme by hand.
		public static ChatItemLinkScan In(string text)
		{
			if (string.IsNullOrEmpty(text))
				return new ChatItemLinkScan([], 0);

			var links = new List<ItemRefLink>();
			var rejected = 0;

			var at = text.IndexOf(ItemRefLinkCodec.Scheme, StringComparison.Ordinal);
			while (at >= 0)
			{
				var open = text.LastIndexOf("<a ", at, StringComparison.Ordinal);
				var close = text.IndexOf("</a>", at, StringComparison.Ordinal);
				if (open >= 0 && close > open)
				{
					try
					{
						links.Add(ItemRefLinkCodec.Parse(text[open..(close + 4)]));
					}
					catch (CodecParseException)
					{
						rejected++;
					}
				}
				else
				{
					rejected++; // the scheme with no anchor around it
				}

				at = text.IndexOf(ItemRefLinkCodec.Scheme, at + ItemRefLinkCodec.Scheme.Length, StringComparison.Ordinal);
			}

			return new ChatItemLinkScan(links, rejected);
		}
	}

	// What one chat message's item links came to.
	public sealed record ChatItemLinkScan(IReadOnlyList<ItemRefLink> Links, int Rejected);
}
