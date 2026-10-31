using System.IO;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The access-window region of the FullChar snapshot tails is emitted from the typed AccessWindowCatalog (the 86
	// PfAccessWindowEntry records). These prove the typed catalog serializes byte-for-byte to the captured region
	// carried in BeachTailsTemplate.bin, which is the oracle for that region.
	public class AccessWindowCatalogTests
	{
		private static string TemplatePath => TestPaths.Asset("BeachTailsTemplate.bin");

		[Fact]
		public void Catalog_HasTheExpectedShape()
		{
			Assert.Equal(86, AccessWindowCatalog.Entries.Count);
			// Every entry is a real PFAccessWindow_ playfield record (the catalog is the game-static reference data).
			Assert.All(AccessWindowCatalog.Entries, e => Assert.StartsWith("PFAccessWindow_", e.InternalName));
		}

		[Fact]
		public void Catalog_SerializesByteExactToTheCapturedRegion()
		{
			var template = FullCharSnapshotSubClassCodec.Parse(File.ReadAllBytes(TemplatePath));

			// The catalog is the same 86 entries the template parses into.
			Assert.Equal(template.AccessWindowEntries.Count, AccessWindowCatalog.Entries.Count);

			var fromTemplate = PfAccessWindowEntryCodec.Build(template.AccessWindowEntries);
			var fromCatalog = PfAccessWindowEntryCodec.Build(AccessWindowCatalog.Entries);

			Assert.Equal(fromTemplate, fromCatalog);
		}
	}
}
