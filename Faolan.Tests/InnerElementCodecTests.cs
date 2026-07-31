using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The remoteformat INNER element - what a `<parameter>` carries, and what a world object's display slot carries.
	// These were the last serialized-XML blobs in the model; storing them as columns is only safe if the columns
	// rebuild the captured bytes EXACTLY, because the client is fed them verbatim (the `key` is a client-validated
	// content hash and `flags` drives the localized declension).
	public class InnerElementCodecTests
	{
		[Theory]
		// An LDB reference by name token, with an entity-escaped key - the dominant dialogue-parameter shape.
		[InlineData("<localized token=\"Stranger\" category=\"100\" flags=\"\" key=\"&#39;TXPNx_CPtJl(C`-&#36;1/z\" >")]
		// ...and by numeric id, with non-empty flags (the grammatical declension).
		[InlineData("<localized id=\"15148\" category=\"51000\" flags=\"m\" key=\"+u#I9(w2ZeOsi!lii6qs\" >")]
		// A literal - both the blanked session-slot form and a populated one.
		[InlineData("<stringentry string=\"\" flags=\"\" >")]
		[InlineData("<stringentry string=\"Valeria\" flags=\"f\" >")]
		// A nested parameter-less root; note the DOUBLE space before '>'.
		[InlineData("<remoteformat id=\"3217121\" category=\"50200\" key=\"0@5S/y3Ly5j(Wq*H1/Y3\" knubot=\"0\"  ></remoteformat>")]
		public void Parse_ThenBuild_IsByteExact(string xml)
		{
			var element = InnerElementCodec.Parse(xml);

			Assert.NotNull(element);
			Assert.Equal(xml, InnerElementCodec.Build(element));
		}

		[Fact]
		public void Parse_ReadsEachShapeIntoTheRightColumns()
		{
			var byToken = InnerElementCodec.Parse("<localized token=\"Stranger\" category=\"100\" flags=\"\" key=\"K\" >");
			Assert.Equal(InnerElementKind.Localized, byToken.Kind);
			Assert.Equal("Stranger", byToken.Token);
			Assert.Null(byToken.Id);
			Assert.Equal(100u, byToken.Category);

			var byId = InnerElementCodec.Parse("<localized id=\"15148\" category=\"51000\" flags=\"m\" key=\"K\" >");
			Assert.Equal(15148u, byId.Id);
			Assert.Null(byId.Token);
			Assert.Equal("m", byId.Flags);

			var literal = InnerElementCodec.Parse("<stringentry string=\"Sonja\" flags=\"f\" >");
			Assert.Equal(InnerElementKind.StringEntry, literal.Kind);
			Assert.Equal("Sonja", literal.Text);

			var nested = InnerElementCodec.Parse("<remoteformat id=\"7\" category=\"50200\" key=\"K\" knubot=\"0\"  ></remoteformat>");
			Assert.Equal(InnerElementKind.RemoteFormat, nested.Kind);
			Assert.Equal(0u, nested.Knubot);
		}

		// Anything outside the three shapes must FAIL rather than be half-read - there is no blob column left, so a
		// throw is the only way an unknown shape can surface at import (#530 rule 4; this used to return null).
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("plain text")]
		[InlineData("<localized token=\"T\" category=\"100\" key=\"K\" >")] // no flags
		[InlineData("<stringentry string=\"S\" >")] // no flags
		[InlineData("<remoteformat id=\"7\" category=\"1\" key=\"K\" knubot=\"0\" ></remoteformat>")] // single space, not the antitag slot
		public void Parse_RejectsAnythingOutsideTheGrammar(string? xml)
		{
			Assert.Throws<CodecParseException>(() => InnerElementCodec.Parse(xml!));
		}

		// Whole-table CONSISTENCY, not fidelity. Every shipped row must produce an element the parser reads back
		// identically - which catches a row whose columns cannot express a legal element at all (a Localized row with
		// neither id nor token, say).
		//
		// It deliberately does NOT claim byte-exactness against the capture, because it cannot: the serialized blobs
		// were dropped when these columns replaced them, so there is nothing left in the database to compare to. The
		// value it builds from is our own output, and a test fed its own output proves only self-consistency. FIDELITY
		// is gated by the [Theory] cases above - those strings are real captured values, one per shape in the corpus -
		// and, for the dialogue path, by ConversationConstructTests against captured packets. The full 8232-row
		// comparison against the original blobs was run once, at migration time, before they were dropped.
		[Fact]
		public async Task EveryShippedInnerElement_IsExpressibleAndSelfConsistent()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var parameters = await db.ConversationScreenParameters.AsNoTracking().ToListAsync(ct);
			var objects = await db.WorldObjects.AsNoTracking().ToListAsync(ct);
			Assert.NotEmpty(parameters);
			Assert.NotEmpty(objects);

			foreach (var raw in parameters.Select(ConversationScreenColumns.InnerRawOf).Concat(objects.Select(WorldObjectDisplay.ToWire)).Where(x => !string.IsNullOrEmpty(x)))
			{
				var element = InnerElementCodec.Parse(raw!); // throws when the element is outside the known grammar
				Assert.Equal(raw, InnerElementCodec.Build(element));
			}
		}
	}
}
