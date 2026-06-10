using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	public class SpawnPacketBuilderTests
	{
		// A different position/handle changes only the mapped bytes and nothing else (length is preserved).
		[Fact]
		public void BuildSpawnBody_WithNewValues_OnlyChangesMappedFields()
		{
			var template = SpawnPacketBuilder.PlayerTemplateBytes;
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
			var template = SpawnPacketBuilder.PlayerTemplateBytes;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			var position = new Vector3(ReadLE(template, SpawnPacketBuilder.PosXOffset), ReadLE(template, SpawnPacketBuilder.PosYOffset), ReadLE(template, SpawnPacketBuilder.PosZOffset));
			var rotation = new Quaternion(ReadLE(template, SpawnPacketBuilder.RotXOffset), ReadLE(template, SpawnPacketBuilder.RotYOffset), ReadLE(template, SpawnPacketBuilder.RotZOffset),
				ReadLE(template, SpawnPacketBuilder.RotWOffset));

			var built = SpawnPacketBuilder.BuildSpawnBody(template, handle, position, rotation);

			Assert.Equal(template, built);
		}

		// Different-length rename grows/shrinks the body and adds the same delta to both enclosing length
		// prefixes and the body length field.
		[Theory, InlineData("Bob", -2), InlineData("Conanofcimmeria", 10)]
		public void SetSpawnName_DifferentLength_AdjustsEnclosingLengths(string name, int expectedDelta)
		{
			var template = SpawnPacketBuilder.PlayerTemplateBytes;
			var renamed = SpawnPacketBuilder.SetSpawnName(template, name);

			Assert.Equal(template.Length + expectedDelta, renamed.Length);
			AssertBeDelta(template, renamed, 0, expectedDelta);
			AssertBeDelta(template, renamed, SpawnPacketBuilder.NameOuterLengthOffset, expectedDelta);
			AssertBeDelta(template, renamed, SpawnPacketBuilder.NameInnerLengthOffset, expectedDelta);
			Assert.Equal(name, Encoding.ASCII.GetString(renamed, SpawnPacketBuilder.NameTagOffset + 2, name.Length));
			Assert.Equal((byte)name.Length, renamed[SpawnPacketBuilder.NameTagOffset + 1]);
		}

		// Same-length rename ("Strie" -> "Conan", both 5) is byte-identical except the 5 name bytes (no length math).
		[Fact]
		public void SetSpawnName_SameLength_OnlyChangesNameBytes()
		{
			var template = SpawnPacketBuilder.PlayerTemplateBytes;
			var renamed = SpawnPacketBuilder.SetSpawnName(template, "Conan");

			Assert.Equal(template.Length, renamed.Length);
			var diffs = 0;
			for (var i = 0; i < template.Length; i++)
			{
				if (template[i] != renamed[i])
					diffs++;
			}

			Assert.Equal(5, diffs);
			Assert.Equal("Conan", Encoding.ASCII.GetString(renamed, SpawnPacketBuilder.NameTagOffset + 2, 5));
		}

		// No-op when the name is unchanged ("Strie" -> "Strie"): byte-identical.
		[Fact]
		public void SetSpawnName_UnchangedName_IsByteIdentical()
		{
			var template = SpawnPacketBuilder.PlayerTemplateBytes;
			Assert.Equal(template, SpawnPacketBuilder.SetSpawnName(template, "Strie"));
		}

		private static void AssertBeDelta(byte[] before, byte[] after, int offset, int delta)
		{
			var b = BinaryPrimitives.ReadUInt32BigEndian(before.AsSpan(offset, 4));
			var a = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(offset, 4));
			Assert.Equal((uint)(b + delta), a);
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
		}
	}
}
