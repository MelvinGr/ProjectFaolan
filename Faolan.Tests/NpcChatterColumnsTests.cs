using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs.Text;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Ambient NPC barks are stored COLUMNIZED, like their StockPhrase sibling (#410): the captured
	//   <remoteformat token="sayinchat(16)|conversation1(3)|RenownBystanderLovers" category="50015" key="..." knubot="0"  >
	// is lifted into typed columns and rebuilt at send time (WorldBroadcaster -> RemoteFormatXml.BuildChatter), so
	// there is no verbatim blob left to replay. These tests are what makes that safe: the constructed string must be
	// byte-identical to what retail sent, because the client is fed these bytes as-is - the `key` is a
	// client-validated content hash and the token addresses a specific line in a specific conversation.
	public class NpcChatterColumnsTests
	{
		// A real captured bark (template 11852, the RenownBystanderLovers group). Note the XML entity in the key and
		// the DOUBLE space before '>' - the empty antitag slot. Both must survive the round trip untouched.
		private const string CapturedBark =
			"<remoteformat token=\"sayinchat(16)|conversation1(3)|RenownBystanderLovers\" category=\"50015\" key=\"]&#39;siB8WQ*b25XY8J7Gd*\" knubot=\"0\"  ></remoteformat>";

		[Fact]
		public void ParseChatter_ThenBuild_ReproducesTheCapturedBark_ByteForByte()
		{
			var row = RemoteFormatXml.ParseChatter(CapturedBark);

			Assert.NotNull(row);
			Assert.Equal(16u, row.SayInChat);
			Assert.Equal("conversation1", row.Channel);
			Assert.Equal(3u, row.ChannelCount);
			Assert.Equal("RenownBystanderLovers", row.Group);
			Assert.Equal(50015u, row.Category);
			Assert.Equal(0u, row.Knubot);
			Assert.Empty(row.Parameters);

			// The key keeps its XML entity exactly as captured - re-escaping is not round-trip safe.
			Assert.Equal("]&#39;siB8WQ*b25XY8J7Gd*", row.FormatKey);
			Assert.Equal(CapturedBark, RemoteFormatXml.BuildChatter(row));
		}

		// The `sendtell` crier bark, the only captured one with a parameter - and its value is the CAPTURING PLAYER'S
		// character name. Two things have to hold at once: the columns must reproduce the captured string exactly
		// when handed that value back (so the import gate can prove nothing was lost), and the row itself must carry
		// only the slot NAME, so the real name never reaches faolan.sql (#417). Its channel is also why Channel is a
		// string rather than a parsed conversation index - "sendtell" has no number.
		[Fact]
		public void ParseChatter_KeepsTheDetectedCharSlotButNotItsCapturedValue()
		{
			const string captured =
				"<remoteformat token=\"sayinchat(57)|sendtell(3)|RenownCrier\" category=\"50015\" key=\".&#39;eMV&#92;79&quot;!GSlWOqs*sl\" knubot=\"0\"  ><parameter value=\"7;Sonja\" name=\"DetectedChar.name\" /></remoteformat>";

			var row = RemoteFormatXml.ParseChatter(captured, out var parameterValue);

			Assert.NotNull(row);
			Assert.Equal("sendtell", row.Channel);
			Assert.Equal(57u, row.SayInChat);
			Assert.Equal("RenownCrier", row.Group);
			Assert.Equal("DetectedChar.name", Assert.Single(row.Parameters).Name);
			Assert.Null(Assert.Single(row.Parameters).Value); // session slot - the captured name is NOT stored
			Assert.Equal("Sonja", parameterValue);

			// Byte-exact when the captured value is handed back - what the importer gates on.
			Assert.Equal(captured, RemoteFormatXml.BuildChatter(row, parameterValue));

			// ...but the row alone renders the slot EMPTY, carrying no captured name.
			Assert.DoesNotContain("Sonja", RemoteFormatXml.BuildChatter(row), StringComparison.Ordinal);
			Assert.Contains("name=\"DetectedChar.name\"", RemoteFormatXml.BuildChatter(row), StringComparison.Ordinal);

			// And a live player's name renders into the slot at send time.
			Assert.Contains("7;Valeria", RemoteFormatXml.BuildChatter(row, "Valeria"), StringComparison.Ordinal);
		}

		// Anything that is not a bark must FAIL to parse rather than be half-read into columns - the importer turns a
		// null into a rejected import, which is the only way an undecodable line surfaces at all now that there is no
		// blob to fall back on.
		[Theory]
		[InlineData("")]
		[InlineData("not xml at all")]
		[InlineData("<remoteformat id=\"5\" category=\"50015\" key=\"k\" knubot=\"0\"  ></remoteformat>")] // a screen, not a bark
		[InlineData("<remoteformat token=\"conversation1(3)|RenownBystanderLovers\" category=\"50015\" key=\"k\" knubot=\"0\"  ></remoteformat>")] // no sayinchat
		[InlineData("<remoteformat token=\"sayinchat(1)|conversation1(3)|Grp\" category=\"50015\" key=\"k\" knubot=\"0\" >")] // truncated: no closing tag
		public void ParseChatter_RejectsAnythingThatIsNotABark(string xml)
		{
			Assert.Null(RemoteFormatXml.ParseChatter(xml));
		}

		// #493: the Damsel in Distress bark carries TWO slots, one of them CONTENT. Before this the grammar allowed
		// one and the importer aborted the entire run on it. The two must round-trip together AND be treated
		// differently: `Target.Name` is the addressed player (regenerated, never stored), while `helptext` is a
		// `<localized>` element that IS the line's content and must survive into the DB.
		[Fact]
		public void ParseChatter_KeepsBothSlotsOfATwoParameterBark_ContentStoredSessionBlanked()
		{
			const string captured =
				"<remoteformat token=\"sayinchat(9)|event(2)|RnBLadyInDistressBrainScript\" category=\"50015\" key=\"!i*X_QMyCa&#39;5_jz!CSRU\" knubot=\"0\"  >"
				+ "<parameter value=\"7;Sonja\" name=\"Target.Name\" />"
				+ "<parameter value=\"7;&lt;localized token=&quot;helptext(8)|RnBLadyInDistressBrainScript|initialvalue&quot; category=&quot;50025&quot; flags=&quot;&quot; key=&quot;Q(0WTe92/HbY`P?vHEv@&quot; &gt;\" name=\"helptext\" />"
				+ "</remoteformat>";

			var row = RemoteFormatXml.ParseChatter(captured, out var parameterValue);

			Assert.NotNull(row);
			Assert.Equal("event", row.Channel);
			Assert.Equal(9u, row.SayInChat);
			Assert.Equal("RnBLadyInDistressBrainScript", row.Group);
			Assert.Equal(2, row.Parameters.Count);

			var slots = row.Parameters.OrderBy(x => x.Ordinal).ToList();
			Assert.Equal("Target.Name", slots[0].Name);
			Assert.Null(slots[0].Value); // session state - the capturing player's name is NOT stored (#417)
			Assert.Equal("helptext", slots[1].Name);
			Assert.Contains("localized", slots[1].Value!, StringComparison.Ordinal); // content - kept verbatim
			Assert.Equal("Sonja", parameterValue);

			// Byte-exact when the captured session value is handed back - what the import gate requires.
			Assert.Equal(captured, RemoteFormatXml.BuildChatter(row, parameterValue));

			// The row alone renders the session slot empty but keeps the content slot intact, and ORDER is preserved.
			var alone = RemoteFormatXml.BuildChatter(row);
			Assert.DoesNotContain("Sonja", alone, StringComparison.Ordinal);
			Assert.Contains("name=\"helptext\"", alone, StringComparison.Ordinal);
			Assert.True(alone.IndexOf("Target.Name", StringComparison.Ordinal) < alone.IndexOf("helptext", StringComparison.Ordinal));
		}

		// Whole-table CONSISTENCY, not fidelity - the same distinction as InnerElementXmlTests. The value it builds
		// from is our own output, so it proves the columns express a legal bark, not that they match retail. Fidelity
		// is the two [Fact] cases above, whose strings are real captured barks.
		[Fact]
		public async Task EveryShippedBark_IsExpressibleAndSelfConsistent()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.NpcChatter.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
			Assert.NotEmpty(rows);

			foreach (var row in rows)
			{
				var built = RemoteFormatXml.BuildChatter(row);
				var reparsed = RemoteFormatXml.ParseChatter(built);
				Assert.NotNull(reparsed);
				Assert.Equal(built, RemoteFormatXml.BuildChatter(reparsed));
				Assert.Equal(50015u, row.Category); // the ambient-chatter category, uniform across the corpus
			}
		}
	}
}
