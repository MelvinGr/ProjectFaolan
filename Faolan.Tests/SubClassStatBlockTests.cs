using System.IO;
using System.Linq;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The relocated fresh-character stat skeleton (SubClassStatBlock) reproduces the beach template's PostAccessBlock
	// byte-exact - so the stat region can be authored without BeachTailsTemplate.bin (decode_statblock.py).
	public class SubClassStatBlockTests
	{
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");

		[Fact]
		public void CreateFreshBlock_MatchesTheTemplateStatBlock()
		{
			var parsed = FullCharSnapshotSubClassCodec.Parse(File.ReadAllBytes(TemplatePath)).PostAccessBlock;
			Assert.NotNull(parsed);

			var fresh = SubClassStatBlock.CreateFreshBlock();

			// Field by field, so a mismatch says which part of the block drifted, then byte-exact so nothing the
			// model does not compare can drift silently.
			Assert.True(parsed.Displayed.Pairs.SequenceEqual(fresh.Displayed.Pairs), "StatList#1 must equal the template's displayed stats");
			Assert.True(parsed.Sheet.Pairs.SequenceEqual(fresh.Sheet.Pairs), "StatList#2 must equal the template's sheet");
			Assert.Equal(parsed.KnownSpells?.Records, fresh.KnownSpells?.Records);
			Assert.Equal(parsed.WideTail, fresh.WideTail);
			Assert.Equal(parsed.PairTail, fresh.PairTail);
			Assert.Equal(SubClassPostAccessBlockCodec.Build(parsed), SubClassPostAccessBlockCodec.Build(fresh));
			Assert.True(fresh.HasRepeatedStatTable); // so the caller's vitals patches fire (they write both copies)
		}
	}
}
