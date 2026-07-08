using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #244: NpcSpawnBuilder.BuildFromScratch assembles the whole FA015807 body with NO captured template (header +
	// transform-with-token + pre-IIR + CoreChar header + message + post-IIR). It must be byte-identical to the
	// freshSpawn TryBuild output (which regenerates the same scaffolds but off a template) for animals and
	// clean-appearance humanoids - proving Data is no longer needed for the frame.
	public class NpcSpawnFromScratchTests
	{
		[Theory]
		[InlineData("SpawnNpcCrocodile")] // animal (minimal post-IIR)
		[InlineData("SpawnNpcCasilda")] // humanoid (appearance/morph post-IIR)
		public void BuildFromScratch_MatchesFreshSpawnBuild(string golden)
		{
			var captured = Golden.Bytes(golden);
			var handle = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(SpawnPacketBuilder.HandleOffset, 4));

			// The freshSpawn TryBuild output is the oracle: same regenerated scaffolds, but assembled off the template.
			var row = RowFromCapturedBody(captured);
			if (NpcSpawnAppearanceCodec.TryDecodeAppearance(captured, out var rowMorphs))
				foreach (var m in rowMorphs)
					row.Morphs.Add(new NpcSpawnMorph { Ordinal = row.Morphs.Count, MorphId = m.MorphId, Value = m.Value, Text = m.Text });
			Assert.True(NpcSpawnBuilder.TryBuild(row, handle, out var expected, freshSpawn: true));

			// Decompose the oracle into the inputs BuildFromScratch takes, then reassemble from scratch.
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(expected, out var read));
			var x = NpcStatCoreCodec.ReadIirHeaderWord(expected)!.Value;
			var position = new Vector3(ReadLE(expected, SpawnPacketBuilder.PosXOffset), ReadLE(expected, SpawnPacketBuilder.PosYOffset), ReadLE(expected, SpawnPacketBuilder.PosZOffset));
			var rotation = new Quaternion(ReadLE(expected, SpawnPacketBuilder.RotXOffset), ReadLE(expected, SpawnPacketBuilder.RotYOffset), ReadLE(expected, SpawnPacketBuilder.RotZOffset), ReadLE(expected, SpawnPacketBuilder.RotWOffset));
			var token = ReadPlayfieldToken(expected);
			var humanoid = NpcSpawnAppearanceCodec.TryDecodeAppearance(expected, out var morphs);

			var actual = NpcSpawnBuilder.BuildFromScratch(handle, x, position, rotation, token, read.Message, humanoid, morphs);

			Assert.Equal(expected, actual);
		}

		// The playfield token: a 4-byte LEB128 varint at [0x18] right after the constructed transform (offset 60).
		private static uint ReadPlayfieldToken(byte[] body)
		{
			var o = SpawnPacketBuilder.RotWOffset + 4; // 60 - the 0x18 tag
			Assert.Equal(0x18, body[o]);
			return (uint)((body[o + 1] & 0x7F) | ((body[o + 2] & 0x7F) << 7) | ((body[o + 3] & 0x7F) << 14) | (body[o + 4] << 21));
		}

		private static float ReadLE(byte[] b, int offset) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));

		private static NpcSpawn RowFromCapturedBody(byte[] body)
		{
			var data = new byte[12 + body.Length]; // headerLen = 0
			body.CopyTo(data, 12);
			return new NpcSpawn
			{
				Data = data,
				PositionX = ReadLE(body, SpawnPacketBuilder.PosXOffset),
				PositionY = ReadLE(body, SpawnPacketBuilder.PosYOffset),
				PositionZ = ReadLE(body, SpawnPacketBuilder.PosZOffset),
				RotationX = ReadLE(body, SpawnPacketBuilder.RotXOffset),
				RotationY = ReadLE(body, SpawnPacketBuilder.RotYOffset),
				RotationZ = ReadLE(body, SpawnPacketBuilder.RotZOffset),
				RotationW = ReadLE(body, SpawnPacketBuilder.RotWOffset)
			};
		}
	}
}
