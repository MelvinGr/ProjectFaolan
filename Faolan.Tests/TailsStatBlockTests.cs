using System.IO;
using System.Linq;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// The relocated fresh-character stat skeleton (TailsStatBlock) reproduces the beach template's PostAccessBlock
	// byte-exact - so the stat region can be authored without BeachTailsTemplate.bin (#186, decode_statblock.py).
	public class TailsStatBlockTests
	{
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin";

		[Fact]
		public void CreateFreshBlock_MatchesTheTemplateStatBlock()
		{
			var parsed = SnapshotTails.Deserialize(File.ReadAllBytes(TemplatePath)).PostAccessBlock;
			Assert.NotNull(parsed);

			var fresh = TailsStatBlock.CreateFreshBlock();

			Assert.Equal(parsed.Marker0, fresh.Marker0);
			Assert.Equal(parsed.Marker1, fresh.Marker1);
			Assert.Equal(parsed.BlockType, fresh.BlockType);
			Assert.True(parsed.Values.SequenceEqual(fresh.Values), "skeleton values must equal the template stat block");
			Assert.True(parsed.Trailer.SequenceEqual(fresh.Trailer), "trailer must match");
			Assert.NotNull(fresh.LargestAdjacentRepeatedRun); // so the caller's vitals patches fire
		}
	}
}
