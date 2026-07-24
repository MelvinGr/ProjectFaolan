using System;
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
			Assert.Null(row.ParameterName);

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
			Assert.Equal("DetectedChar.name", row.ParameterName);
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

		// Every shipped row must rebuild to something the parser reads back identically - the whole-table version of
		// the gate above, run against the fixture built from faolan.sql.
		[Fact]
		public async Task EveryShippedBark_RoundTripsThroughItsColumns()
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
