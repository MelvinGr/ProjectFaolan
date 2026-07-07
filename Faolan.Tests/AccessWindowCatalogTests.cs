using System.IO;
using Faolan.Core.Network.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// #258: the access-window region of the FullChar snapshot tails is now emitted from the TYPED AccessWindowCatalog
	// (the 86 PfAccessWindowEntry records) instead of the captured AccessWindowTable.bin blob. These prove the typed
	// catalog serializes byte-for-byte to the captured region carried in BeachTailsTemplate.bin (the surviving oracle),
	// so retiring the .bin changed nothing on the wire.
	public class AccessWindowCatalogTests
	{
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan.Tests/Assets/BeachTailsTemplate.bin";

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
			var template = FullCharSnapshotSubClass.Deserialize(File.ReadAllBytes(TemplatePath));

			// The catalog is the same 86 entries the template parses into.
			Assert.Equal(template.AccessWindowEntries.Count, AccessWindowCatalog.Entries.Count);

			var fromTemplate = FullCharSnapshotSubClass.SerializeAccessWindowEntries(template.AccessWindowEntries);
			var fromCatalog = FullCharSnapshotSubClass.SerializeAccessWindowEntries(AccessWindowCatalog.Entries);

			Assert.Equal(fromTemplate, fromCatalog);
		}
	}
}
