using Faolan.Core.Network.Server.ChatServer;
using Xunit;

namespace Faolan.Tests
{
	// ChatMarkup - the chat/system-line type. The client renders these as markup, so the DEFAULT has to be escaping:
	// the reason this type exists is that every call site used to hand-write "&lt;handle&gt;", and any text that came
	// from a PLAYER went to other clients verbatim.
	public class ChatMarkupTests
	{
		[Fact]
		public void ImplicitConversionFromString_Escapes()
		{
			// The whole point: a plain usage string now survives to the client as typed.
			ChatMarkup message = "Usage: .kill [<handle> | self]";

			Assert.Equal("Usage: .kill [&lt;handle&gt; | self]", message.Value);
		}

		[Fact]
		public void InterpolatedValues_AreEscapedToo()
		{
			// The dangerous case: the hole content is attacker-controlled (a player name, a map search, an exception).
			var playerSuppliedName = "<span style=\"color: red;\">Admin</span>";
			ChatMarkup message = $"No account or character named '{playerSuppliedName}'.";

			Assert.DoesNotContain("<span", message.Value);
			Assert.Contains("&lt;span", message.Value);
		}

		[Fact]
		public void Raw_PassesMarkupThrough()
		{
			Assert.Equal("<b>bold</b>", ChatMarkup.Raw("<b>bold</b>").Value);
		}

		[Fact]
		public void Color_EscapesTheTextButKeepsTheSpan()
		{
			var m = ChatMarkup.Color("red", "map <none> missing");

			Assert.Equal("<span style=\"color: red;\">map &lt;none&gt; missing</span>", m.Value);
		}

		[Fact]
		public void Bold_EscapesTheText()
		{
			Assert.Equal("<b>a &amp; b</b>", ChatMarkup.Bold("a & b").Value);
		}

		[Fact]
		public void Lines_EscapesPlainLines_AndKeepsOptedInMarkup()
		{
			var m = ChatMarkup.Lines(ChatMarkup.Bold("Heading <x>"), "detail <y>");

			Assert.Equal("<b>Heading &lt;x&gt;</b><br />detail &lt;y&gt;", m.Value);
		}

		[Fact]
		public void Escape_DoesNotDoubleEscape_BecauseAmpersandGoesFirst()
		{
			// Ampersand must be replaced before the others, or "&lt;" produced by the '<' pass would become "&amp;lt;".
			Assert.Equal("&lt;a&gt;", ChatMarkup.Escape("<a>"));
			Assert.Equal("&amp;lt;", ChatMarkup.Escape("&lt;")); // already-escaped input escapes ONCE more, visibly
		}

		[Theory]
		[InlineData(null, "")]
		[InlineData("", "")]
		[InlineData("plain text", "plain text")] // fast path: untouched when there is nothing to escape
		[InlineData("a & b", "a &amp; b")]
		// Quotes and apostrophes ride through UNTOUCHED. The client does not decode &apos;, so escaping it put the
		// literal text "&apos;" in the chat window (#448 GM-command reply, 2026-07-29). Settled against the client's
		// own LDB content: &apos; appears 0 times in 2,164,982 strings while 228,544 carry a literal apostrophe.
		[InlineData("say \"hi\"", "say \"hi\"")]
		[InlineData("it's", "it's")]
		[InlineData("'' and \"\"", "'' and \"\"")] // the exact shape that showed as &apos;&apos;
		public void Escape_Cases(string? input, string expected)
		{
			Assert.Equal(expected, ChatMarkup.Escape(input));
		}

		// The injection guard is unchanged by leaving quotes alone: only & < > can open a tag or an entity, so a
		// player-supplied string still cannot inject markup into another player's chat window.
		[Theory]
		[InlineData("<font color=red>fake</font>", "&lt;font color=red&gt;fake&lt;/font&gt;")]
		[InlineData("<br />", "&lt;br /&gt;")]
		[InlineData("it's <b>bold</b>", "it's &lt;b&gt;bold&lt;/b&gt;")]
		public void Escape_StillNeutralisesMarkup(string input, string expected)
		{
			Assert.Equal(expected, ChatMarkup.Escape(input));
			Assert.DoesNotContain("<", ChatMarkup.Escape(input));
		}
	}
}
