using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	public class SpawnPacketBuilderTests
	{
		// A different position/handle changes only the mapped bytes and nothing else (length is preserved).
		[Fact]
		public void BuildSpawnBody_WithNewValues_OnlyChangesMappedFields()
		{
			var template = Golden.Bytes("SpawnAssassinTemplate");
			var built = SpawnPacketBuilder.BuildSpawnBody(template, 0x02000001u, new Vector3(100f, 50f, 200f), Quaternion.Identity);

			Assert.Equal(template.Length, built.Length);
			Assert.Equal(0x02000001u, built.ReadU32Be(SpawnPacketBuilder.HandleOffset));
			Assert.Equal(100f, built.ReadF32Le(SpawnPacketBuilder.PosXOffset));
			Assert.Equal(200f, built.ReadF32Le(SpawnPacketBuilder.PosZOffset));
		}

		// Round-trip gate: rebuilding the player spawn from the template's own handle/position/rotation must reproduce
		// the captured bytes exactly. That is what validates the field offsets - a mis-mapped offset corrupts the body
		// and fails here rather than in a client.
		[Fact]
		public void BuildSpawnBody_WithTemplateValues_IsByteIdentical()
		{
			var template = Golden.Bytes("SpawnAssassinTemplate");

			var handle = template.ReadU32Be(SpawnPacketBuilder.HandleOffset);
			var position = new Vector3(template.ReadF32Le(SpawnPacketBuilder.PosXOffset), template.ReadF32Le(SpawnPacketBuilder.PosYOffset), template.ReadF32Le(SpawnPacketBuilder.PosZOffset));
			var rotation = new Quaternion(template.ReadF32Le(SpawnPacketBuilder.RotXOffset), template.ReadF32Le(SpawnPacketBuilder.RotYOffset), template.ReadF32Le(SpawnPacketBuilder.RotZOffset),
				template.ReadF32Le(SpawnPacketBuilder.RotWOffset));

			var built = SpawnPacketBuilder.BuildSpawnBody(template, handle, position, rotation);

			Assert.Equal(template, built);
		}

	}
}
