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
	///     Only the three entities the client actually decodes are replaced, in an order that cannot double-escape
	///     (ampersand first) - see <see cref="Escape" />, which explains why quotes and apostrophes are left alone.
	///     Nothing else is touched: the payload is a length-prefixed LATIN-1 string on the wire, not a URL or a JS
	///     context, so ampersand/angle escaping is the whole job.
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

		/// <summary>
		///     The three entities the client actually decodes, ampersand first so an escape can never be escaped twice.
		/// </summary>
		/// <remarks>
		///     ⚠️ <b>Quotes and apostrophes are deliberately NOT escaped</b> - escaping them is visible corruption. The
		///     client's renderer does not decode <c>&amp;apos;</c>, so <c>'x'</c> reached the chat window as the literal
		///     text <c>&amp;apos;x&amp;apos;</c> (seen 2026-07-29 on the #448 GM-command reply).
		///     <para>
		///         Settled against the client's own content rather than by taste - across all 2,164,982 LDB strings:
		///         <c>&amp;apos;</c> appears <b>0</b> times while 228,544 strings carry a literal <c>'</c>, and
		///         <c>&amp;quot;</c> appears 7 times against 183,200 carrying a literal <c>"</c> (the client writes
		///         <c>You can "auto-complete" the group name</c> with markup in the same line). Angle brackets are the
		///         opposite: <c>&amp;lt;</c>/<c>&amp;gt;</c> appear 81 times each, exactly where a literal bracket must not
		///         parse as a tag - <c>&amp;lt;no target&amp;gt;</c>, <c>&lt;font color=red&gt;Usage: /emote
		///         &amp;lt;emote-name&amp;gt;&lt;/font&gt;</c> - which is the positive proof that those three ARE decoded.
		///     </para>
		///     That keeps the injection guard intact: <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> are the only characters
		///     that can open a tag or an entity, so escaping them is the whole job. A quote is inert in element content,
		///     and no call site interpolates untrusted text into an attribute (<see cref="Color" /> takes a
		///     developer-supplied colour).
		/// </remarks>
		public static string Escape(string? text)
		{
			if (string.IsNullOrEmpty(text))
				return "";

			// Fast path: the overwhelming majority of lines carry none of these at all.
			if (text.IndexOfAny(['&', '<', '>']) < 0)
				return text;

			return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
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
