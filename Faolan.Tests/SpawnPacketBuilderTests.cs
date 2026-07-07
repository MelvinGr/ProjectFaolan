using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Tests.Support;
using Xunit;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Tests
{
	public class SpawnPacketBuilderTests
	{
		// A different position/handle changes only the mapped bytes and nothing else (length is preserved).
		[Fact]
		public void BuildSpawnBody_WithNewValues_OnlyChangesMappedFields()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");
			var built = SpawnPacketBuilder.BuildSpawnBody(template, 0x02000001u, new Vector3(100f, 50f, 200f), Quaternion.Identity);

			Assert.Equal(template.Length, built.Length);
			Assert.Equal(0x02000001u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(SpawnPacketBuilder.HandleOffset, 4)));
			Assert.Equal(100f, ReadLE(built, SpawnPacketBuilder.PosXOffset));
			Assert.Equal(200f, ReadLE(built, SpawnPacketBuilder.PosZOffset));
		}

		// Round-trip gate (Phase 0): rebuilding the player spawn from the template's own handle/position/rotation
		// must reproduce the captured bytes exactly. This validates the field offsets - any mis-mapped offset
		// corrupts the body and fails here before anything is sent to a client.
		[Fact]
		public void BuildSpawnBody_WithTemplateValues_IsByteIdentical()
		{
			var template = Golden.Bytes("SpawnStrieTemplate");

			var handle = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			var position = new Vector3(ReadLE(template, SpawnPacketBuilder.PosXOffset), ReadLE(template, SpawnPacketBuilder.PosYOffset), ReadLE(template, SpawnPacketBuilder.PosZOffset));
			var rotation = new Quaternion(ReadLE(template, SpawnPacketBuilder.RotXOffset),
				ReadLE(template, SpawnPacketBuilder.RotYOffset),
				ReadLE(template, SpawnPacketBuilder.RotZOffset),
				ReadLE(template, SpawnPacketBuilder.RotWOffset));

			var built = SpawnPacketBuilder.BuildSpawnBody(template, handle, position, rotation);

			Assert.Equal(template, built);
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
		}
	}
}
