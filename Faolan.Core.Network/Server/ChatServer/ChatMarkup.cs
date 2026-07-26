using System;

namespace Faolan.Core.Network.Server.ChatServer
{
	/// <summary>
	///     One line of chat/system text, already in the form the client will render.
	/// </summary>
	/// <remarks>
	///     The AoC client renders chat and system lines as MARKUP, so a bare <c>&lt;</c> in the text is swallowed as a
	///     tag: <c>"Usage: .kill [&lt;handle&gt; | self]"</c> used to have to be hand-escaped at every call site, and
	///     forgetting it silently ate the placeholder. Worse, any text that came from a PLAYER (a say, a whisper, a
	///     channel post) went to other clients verbatim, so one player could inject markup into another's chat window.
	///     <para>
	///         So the chat ops take this type rather than <see cref="string" />, and the implicit conversion from string
	///         ESCAPES. That makes the safe thing the default and the unsafe thing impossible to do by accident:
	///         <c>SendSystemMessage(id, $"target {name}")</c> is escaped (including the interpolated value), while markup
	///         is opted into explicitly with <see cref="Raw" /> - which is then easy to grep for and review.
	///     </para>
	///     Only the five entities XML/HTML actually needs are replaced, in an order that cannot double-escape (ampersand
	///     first). Nothing else is touched: the payload is a length-prefixed LATIN-1 string on the wire, not a URL or a
	///     JS context, so quote/angle/ampersand escaping is the whole job.
	/// </remarks>
	public readonly record struct ChatMarkup(string Value)
	{
		/// <summary>Markup that is ALREADY rendered - passed through verbatim. The deliberate opt-out of escaping.</summary>
		public static ChatMarkup Raw(string markup)
		{
			return new ChatMarkup(markup ?? "");
		}

		/// <summary>Plain text, escaped. Same as the implicit conversion; spell it out where that reads better.</summary>
		public static ChatMarkup Text(string? plain)
		{
			return new ChatMarkup(Escape(plain));
		}

		/// <summary>
		///     <paramref name="plain" /> in a CSS colour, escaped inside the span. The common case by far - most markup in
		///     the codebase is a coloured word in an otherwise plain sentence (<c>ON</c> green, an error red).
		/// </summary>
		public static ChatMarkup Color(string cssColor, string? plain)
		{
			return new ChatMarkup($"<span style=\"color: {cssColor};\">{Escape(plain)}</span>");
		}

		/// <summary><paramref name="plain" /> in bold, escaped inside the tag.</summary>
		public static ChatMarkup Bold(string? plain)
		{
			return new ChatMarkup($"<b>{Escape(plain)}</b>");
		}

		/// <summary>
		///     Joins lines with the client's line break. Each line is a <see cref="ChatMarkup" />, so a plain string line
		///     is escaped by the implicit conversion while a line that needs markup opts in per line - which is what the
		///     multi-line reports (<c>.target</c>, <c>.maps</c>, <c>.help</c>) need: a bold heading over escaped detail.
		/// </summary>
		public static ChatMarkup Lines(params ChatMarkup[] lines)
		{
			return new ChatMarkup(string.Join("<br />", Array.ConvertAll(lines, l => l.Value)));
		}

		/// <summary>The five XML entities, ampersand first so an escape can never be escaped twice.</summary>
		public static string Escape(string? text)
		{
			if (string.IsNullOrEmpty(text))
				return "";

			// Fast path: the overwhelming majority of lines carry none of these at all.
			if (text.IndexOfAny(['&', '<', '>', '"', '\'']) < 0)
				return text;

			return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
		}

		/// <summary>Escapes on the way in - so passing a string anywhere a <see cref="ChatMarkup" /> is expected is safe.</summary>
		public static implicit operator ChatMarkup(string? plain)
		{
			return Text(plain);
		}

		public override string ToString()
		{
			return Value;
		}
	}
}
