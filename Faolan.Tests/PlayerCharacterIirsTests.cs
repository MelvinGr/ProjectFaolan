using System.IO;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The base player-spawn ProtoCoreCharacterIIR is built from code by PlayerCharacterIirs; PlayerCharacterIirTemplate.bin
	// is kept only as the golden oracle these tests compare against. The bare template - ForCharacter with no character -
	// must reproduce the .bin exactly except for the worn-gear entries, which the build always replaces with the
	// character's own gear and therefore deliberately leaves empty.
	public class PlayerCharacterIirsTests
	{
		private static readonly string TemplatePath = TestPaths.Asset("PlayerCharacterIirTemplate.bin");

		[Fact]
		public void BuildBareTemplate_EqualsTheTemplate_ExceptWornGear()
		{
			var template = File.ReadAllBytes(TemplatePath);

			// The template with its worn-gear entries stripped (Build always replaces them per character).
			var stripped = template.FromProtobuf<ProtoCoreCharacterIIR>();
			stripped.InventoryEntries.Clear();
			var strippedBytes = stripped.ToProtobuf();

			var built = PlayerCharacterIirs.ForCharacter(null!, null!).ToProtobuf();

			Assert.Equal(strippedBytes, built);
		}

		// The captured template carries exactly 4 worn-gear entries, which the build deliberately drops. Pinning the
		// count guards the assumption above: a change in the template's shape would otherwise be masked by the
		// equality test.
		[Fact]
		public void Template_CarriesOnlyWornGearThatIsAlwaysReplaced()
		{
			var template = File.ReadAllBytes(TemplatePath);
			var msg = template.FromProtobuf<ProtoCoreCharacterIIR>();
			Assert.Equal(4, msg.InventoryEntries.Count);
			Assert.Empty(PlayerCharacterIirs.ForCharacter(null!, null!).InventoryEntries);
		}
	}
}
