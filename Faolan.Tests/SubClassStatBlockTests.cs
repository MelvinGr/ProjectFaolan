using System.IO;
using System.Linq;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The relocated fresh-character stat skeleton (SubClassStatBlock) reproduces the beach template's PostAccessBlock
	// byte-exact - so the stat region can be authored without BeachTailsTemplate.bin (#186, decode_statblock.py).
	public class SubClassStatBlockTests
	{
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");

		[Fact]
		public void CreateFreshBlock_MatchesTheTemplateStatBlock()
		{
			var parsed = FullCharSnapshotSubClassCodec.Parse(File.ReadAllBytes(TemplatePath)).PostAccessBlock;
			Assert.NotNull(parsed);

			var fresh = SubClassStatBlock.CreateFreshBlock();

			Assert.Equal(parsed.Marker0, fresh.Marker0);
			Assert.Equal(parsed.Marker1, fresh.Marker1);
			Assert.Equal(parsed.BlockType, fresh.BlockType);
			Assert.True(parsed.Values.SequenceEqual(fresh.Values), "skeleton values must equal the template stat block");
			Assert.True(parsed.Trailer.SequenceEqual(fresh.Trailer), "trailer must match");
			Assert.True(fresh.HasRepeatedStatTable); // so the caller's vitals patches fire (they write the +148 copy too)
		}
	}
}
