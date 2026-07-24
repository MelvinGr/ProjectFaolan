using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The remoteformat INNER element - what a `<parameter>` carries, and what a world object's display slot carries.
	// These were the last serialized-XML blobs in the model; storing them as columns is only safe if the columns
	// rebuild the captured bytes EXACTLY, because the client is fed them verbatim (the `key` is a client-validated
	// content hash and `flags` drives the localized declension).
	public class InnerElementXmlTests
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
			var element = InnerElement.Parse(xml);

			Assert.NotNull(element);
			Assert.Equal(xml, element.Build());
		}

		[Fact]
		public void Parse_ReadsEachShapeIntoTheRightColumns()
		{
			var byToken = InnerElement.Parse("<localized token=\"Stranger\" category=\"100\" flags=\"\" key=\"K\" >");
			Assert.Equal(InnerElementKind.Localized, byToken.Kind);
			Assert.Equal("Stranger", byToken.Token);
			Assert.Null(byToken.Id);
			Assert.Equal(100u, byToken.Category);

			var byId = InnerElement.Parse("<localized id=\"15148\" category=\"51000\" flags=\"m\" key=\"K\" >");
			Assert.Equal(15148u, byId.Id);
			Assert.Null(byId.Token);
			Assert.Equal("m", byId.Flags);

			var literal = InnerElement.Parse("<stringentry string=\"Sonja\" flags=\"f\" >");
			Assert.Equal(InnerElementKind.StringEntry, literal.Kind);
			Assert.Equal("Sonja", literal.Text);

			var nested = InnerElement.Parse("<remoteformat id=\"7\" category=\"50200\" key=\"K\" knubot=\"0\"  ></remoteformat>");
			Assert.Equal(InnerElementKind.RemoteFormat, nested.Kind);
			Assert.Equal(0u, nested.Knubot);
		}

		// Anything outside the three shapes must FAIL rather than be half-read - there is no blob column left, so a
		// null is the only way an unknown shape can surface at import.
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("plain text")]
		[InlineData("<localized token=\"T\" category=\"100\" key=\"K\" >")] // no flags
		[InlineData("<stringentry string=\"S\" >")] // no flags
		[InlineData("<remoteformat id=\"7\" category=\"1\" key=\"K\" knubot=\"0\" ></remoteformat>")] // single space, not the antitag slot
		public void Parse_RejectsAnythingOutsideTheGrammar(string xml)
		{
			Assert.Null(InnerElement.Parse(xml));
		}

		// The whole-corpus gate: every inner element the seed ships must round-trip. This is what would catch a new
		// capture introducing a shape the grammar does not cover.
		[Fact]
		public async Task EveryShippedInnerElement_RoundTripsByteExactly()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var parameters = await db.ConversationScreenParameters.AsNoTracking().ToListAsync(ct);
			var objects = await db.WorldObjects.AsNoTracking().ToListAsync(ct);
			Assert.NotEmpty(parameters);
			Assert.NotEmpty(objects);

			foreach (var raw in parameters.Select(ConversationScreenColumns.InnerRawOf).Concat(objects.Select(o => o.DisplayName)).Where(x => !string.IsNullOrEmpty(x)))
			{
				var element = InnerElement.Parse(raw);
				Assert.True(element != null, $"inner element outside the known grammar: {raw}");
				Assert.Equal(raw, element.Build());
			}
		}
	}
}
