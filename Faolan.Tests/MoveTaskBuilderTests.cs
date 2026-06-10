using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.GameServer.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the minimal stationary MoveTask (96C46740) build: reconstructing it from the captured handle +
	// position reproduces the captured 31-byte body byte-for-byte. This is the registration a non-patrolling NPC
	// needs, constructible from the spawn's own position (the full multi-segment patrol form is an enhancement).
	public class MoveTaskBuilderTests
	{
		[Fact]
		public void BuildStationary_FromCapturedHandleAndPosition_IsByteIdentical()
		{
			var captured = Golden.Bytes("MoveTaskStationary");

			var handle = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(12, 4));
			var position = new Vector3(BinaryPrimitives.ReadSingleBigEndian(captured.AsSpan(18, 4)), BinaryPrimitives.ReadSingleBigEndian(captured.AsSpan(22, 4)),
				BinaryPrimitives.ReadSingleBigEndian(captured.AsSpan(26, 4)));

			var built = MoveTaskBuilder.BuildStationary(handle, position);

			Assert.Equal(captured, built);
		}

		[Fact]
		public void BuildStationary_WritesHandleAndPositionBigEndian()
		{
			var built = MoveTaskBuilder.BuildStationary(0x000818D9u, new Vector3(461.22f, 162.71f, 664.95f));

			Assert.Equal(31, built.Length);
			Assert.Equal(MoveTaskBuilder.SubOpcode, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(4, 4)));
			Assert.Equal(0x000818D9u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(12, 4)));
			Assert.Equal(461.22f, BinaryPrimitives.ReadSingleBigEndian(built.AsSpan(18, 4)));
			Assert.Equal((byte)0x06, built[30]);
		}
	}
}
