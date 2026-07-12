using System.Linq;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Text;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact parse/build of a remoteformat dialogue screen (#188): proves a Knubot screen can be GENERATED from
	// structured fields (id/category/key/knubot/antitag + named parameters) byte-for-byte, so the captured XML
	// doesn't have to be replayed. Oracles are real screens from the captured ConversationPackets.
	public class RemoteFormatXmlTests
	{
		// Real AppendText screen with all three inner parameter shapes (localized token, stringentry, localized id).
		private const string AppendXml =
			"<remoteformat id=\"33600\" category=\"50002\" key=\"djaj!xgdVQpMtKta6Csp\" knubot=\"0\" antitag=\"0\" ><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-&amp;#36;1/z&quot; &gt;\" name=\" a\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-&amp;#36;1/z&quot; &gt;\" name=\" p\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-&amp;#36;1/z&quot; &gt;\" name=\" t\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-&amp;#36;1/z&quot; &gt;\" name=\"a\" /><parameter value=\"7;&lt;stringentry string=&quot;&quot; flags=&quot;&quot; &gt;\" name=\"guildname\" /><parameter value=\"7;&lt;stringentry string=&quot;DemoL80&quot; flags=&quot;m&quot; &gt;\" name=\"name\" /><parameter value=\"7;&lt;stringentry string=&quot;Not in guild [Legacy of Cimmeria]&quot; flags=&quot;&quot; &gt;\" name=\"npcguildname\" /><parameter value=\"7;&lt;localized id=&quot;15148&quot; category=&quot;51000&quot; flags=&quot;m&quot; key=&quot;+u#I9(w2ZeOsi!lii6qs&quot; &gt;\" name=\"npcname\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-&amp;#36;1/z&quot; &gt;\" name=\"p\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-&amp;#36;1/z&quot; &gt;\" name=\"t\" /></remoteformat>";

		// Real CloseChatWindow screen (no parameters; note knubot="0"  > = empty antitag slot).
		private const string CloseXml = "<remoteformat id=\"0\" category=\"100\" key=\".fzTuEyiu7B&#92;ZYZMoq1-\" knubot=\"0\"  ></remoteformat>";

		[Fact]
		public void Build_FromScratch_WithLiveStringEntry()
		{
			// Generate a screen with a live player-name parameter built from session data (no captured value).
			var screen = new RemoteFormatXml.Screen("33600", "50002", "djaj!xgdVQpMtKta6Csp", "0", "0",
				[RemoteFormatXml.StringEntry("name", "Conan", "m"), RemoteFormatXml.StringEntry("guildname", "Cimmerians")]);

			var xml = RemoteFormatXml.Build(screen);
			Assert.Contains("string=&quot;Conan&quot; flags=&quot;m&quot;", xml);
			Assert.Contains("string=&quot;Cimmerians&quot;", xml);
			// Round-trips.
			Assert.Equal(xml, RemoteFormatXml.Build(RemoteFormatXml.Parse(xml)));
		}

		[Fact]
		public void Parse_Append_ExtractsStructure()
		{
			var s = RemoteFormatXml.Parse(AppendXml);
			Assert.Equal("33600", s.Id);
			Assert.Equal("50002", s.Category);
			Assert.Equal("0", s.Antitag);
			Assert.Equal(10, s.Parameters.Count);

			var name = s.Parameters.Single(p => p.Name == "name");
			Assert.Contains("stringentry string=\"DemoL80\" flags=\"m\"", name.InnerRaw);
			var npc = s.Parameters.Single(p => p.Name == "npcname");
			Assert.Contains("localized id=\"15148\"", npc.InnerRaw);
		}

		[Fact]
		public void Parse_Close_HasNoAntitagOrParameters()
		{
			var s = RemoteFormatXml.Parse(CloseXml);
			Assert.Null(s.Antitag);
			Assert.Empty(s.Parameters);
			Assert.Equal("100", s.Category);
		}

		[Theory, InlineData(CloseXml), InlineData(AppendXml)]
		public void ParseThenBuild_IsByteExact(string xml)
		{
			var screen = RemoteFormatXml.Parse(xml);
			Assert.Equal(xml, RemoteFormatXml.Build(screen));
		}

		// The DB columnization (#188): a captured screen -> ConversationScreen row (+ parameter children) -> back to
		// the wire XML must be byte-exact, so ScreensJson can be dropped for typed columns. Covers the double-space
		// "knubot=\"0\"  >" (absent antitag) and the full 10-parameter scaffold.
		[Theory, InlineData(CloseXml), InlineData(AppendXml)]
		public void ColumnRow_RoundTrips_ByteExact(string xml)
		{
			var row = ConversationScreenColumns.ToRow(xml, 0);
			Assert.Equal(xml, RemoteFormatXml.Build(ConversationScreenColumns.ToScreen(row)));
		}

		[Fact]
		public void WithLivePlayer_RegeneratesNameAndGuild_KeepingTheRest()
		{
			var s = RemoteFormatXml.WithLivePlayer(RemoteFormatXml.Parse(AppendXml), "Conan", "Cimmerians");
			var xml = RemoteFormatXml.Build(s);

			// Player name regenerated (captured flags="m" preserved), guild filled, the captured player gone.
			Assert.Contains("string=&quot;Conan&quot; flags=&quot;m&quot;", xml);
			Assert.Contains("string=&quot;Cimmerians&quot;", xml);
			Assert.DoesNotContain("DemoL80", xml);
			// NPC name + localized tokens untouched.
			Assert.Contains("localized id=&quot;15148&quot;", xml);
			Assert.Contains("Not in guild [Legacy of Cimmeria]", xml);
			// Root (incl. the harvested key) unchanged.
			Assert.StartsWith("<remoteformat id=\"33600\" category=\"50002\" key=\"djaj!xgdVQpMtKta6Csp\"", xml);
		}
	}
}
