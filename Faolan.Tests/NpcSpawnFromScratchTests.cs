using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #244: NpcSpawnBuilder.BuildBody assembles the whole FA015807 body with NO captured template (header +
	// transform-with-token + pre-IIR + CoreChar header + message + post-IIR). Driven from golden captures, decomposed
	// into the same inputs the live path (TryBuildLive) feeds it: the built body must parse back and carry the expected
	// post-IIR shape - the minimal form for an animal, the appearance/morph table for a humanoid.
	public class NpcSpawnFromScratchTests
	{
		[Fact]
		public void BuildFromScratch_Animal_ParsesAndCarriesMinimalPostIir()
		{
			var golden = Golden.Bytes("SpawnNpcCrocodile");
			var body = BuildFromGolden(golden, out var handle, out var morphs, NpcSpawnScaffold.MinimalWord2);

			Assert.Empty(morphs); // an animal carries no appearance table
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			Assert.Equal(handle, body.ReadU32Be(SpawnPacketBuilder.HandleOffset));
			Assert.Equal(NpcSpawnScaffold.BuildMinimalPostIir(), body[(read.Offset + read.Length)..]);
		}

		[Fact]
		public void BuildFromScratch_Humanoid_ParsesAndCarriesAppearanceTable()
		{
			var golden = Golden.Bytes("SpawnNpcCasilda");
			var body = BuildFromGolden(golden, out _, out var morphs, NpcSpawnScaffold.MinimalWord2);

			Assert.NotEmpty(morphs); // Casilda's appearance/morph table
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			// the post-IIR is the appearance table generated from the decoded morphs (not the minimal form)
			Assert.Equal(NpcSpawnAppearanceCodec.BuildPostIir(morphs), body[(read.Offset + read.Length)..]);
			// and the transform round-trips (position/rotation/token preserved through the build)
			Assert.Equal(golden.AsSpan(SpawnPacketBuilder.PosXOffset, 4).ToArray(), body.AsSpan(SpawnPacketBuilder.PosXOffset, 4).ToArray());
			Assert.Equal(ReadPlayfieldToken(golden), ReadPlayfieldToken(body));
		}

		// Decompose a golden capture into the inputs BuildBody takes (IIR header, transform, token, message,
		// morphs) and reassemble from scratch - the same decomposition TryBuildLive performs off the DB columns.
		private static byte[] BuildFromGolden(byte[] golden, out uint handle, out List<NpcAppearanceMorph> morphs, uint minimalWord2Value)
		{
			handle = golden.ReadU32Be(SpawnPacketBuilder.HandleOffset);
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(golden, out var read));
			var x = NpcStatCoreCodec.ReadIirHeaderWord(golden)!.Value;
			var pos = new Vector3(golden.ReadF32Le(SpawnPacketBuilder.PosXOffset), golden.ReadF32Le(SpawnPacketBuilder.PosYOffset), golden.ReadF32Le(SpawnPacketBuilder.PosZOffset));
			var rot = new Quaternion(golden.ReadF32Le(SpawnPacketBuilder.RotXOffset), golden.ReadF32Le(SpawnPacketBuilder.RotYOffset), golden.ReadF32Le(SpawnPacketBuilder.RotZOffset),
				golden.ReadF32Le(SpawnPacketBuilder.RotWOffset));
			NpcSpawnAppearanceCodec.TryDecodeAppearance(golden, out morphs);
			return NpcSpawnBuilder.BuildBody(handle, x, pos, rot, ReadPlayfieldToken(golden), read.Message, morphs, minimalWord2Value);
		}


		// The playfield token: a 4-byte LEB128 varint at [0x18] right after the constructed transform (offset 60).
		private static uint ReadPlayfieldToken(byte[] body)
		{
			var o = SpawnPacketBuilder.RotWOffset + 4; // 60 - the 0x18 tag
			Assert.Equal(0x18, body[o]);
			return (uint)((body[o + 1] & 0x7F) | ((body[o + 2] & 0x7F) << 7) | ((body[o + 3] & 0x7F) << 14) | (body[o + 4] << 21));
		}
	}
}
