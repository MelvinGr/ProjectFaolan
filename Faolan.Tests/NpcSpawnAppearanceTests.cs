using System;
using System.Buffers.Binary;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the humanoid NPC post-IIR APPEARANCE / morph table codec against a real capture (Casilda). Decoding her
	// post-IIR into morph rows and re-encoding them reproduces the captured bytes exactly - the construction oracle
	// that lets the appearance table generate from the DB so NpcSpawn.Data can be retired (Gitea #185 / #243).
	public class NpcSpawnAppearanceTests
	{
		// Animals carry no appearance table (the 41-byte minimal post-IIR), so the decoder declines them - they
		// stay on the minimal-scaffold path.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther")]
		public void DecodeAppearance_Animal_ReturnsFalse(string golden)
		{
			Assert.False(NpcSpawnAppearanceCodec.TryDecodeAppearance(Golden.Bytes(golden), out var morphs));
			Assert.Empty(morphs);
		}

		[Fact]
		public void DecodeAppearance_Casilda_RoundTripsBuildPostIir()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var capturedPostIir = body[(read.Offset + read.Length)..];

			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var morphs));
			Assert.Equal(14, morphs.Count);

			// The texscript colour ref rides the id 0x10D entry; the rest are plain [morphId][float] morph values.
			var texscript = morphs.Single(m => m.Text.Length > 0);
			Assert.Equal(0x10Du, texscript.MorphId);
			Assert.Equal("*ii=-1;*bl=-1", texscript.Text);

			// Re-encoding the decoded rows reproduces the captured post-IIR byte-for-byte.
			Assert.Equal(capturedPostIir, NpcSpawnAppearanceCodec.BuildPostIir(morphs));
		}

		// #243 lenient decode: a mid-session humanoid capture carries the SAME morph table behind a large RUNTIME
		// record1 instead of the empty 5-byte record1. Strict TryDecodeAppearance declines it; TryDecodeAppearanceLenient
		// skips the runtime record (using firstRecordLen) and recovers the identical table. Synthesize the runtime shape
		// from Casilda's clean capture (swap her empty record1 for a runtime block, bump firstRecordLen, recordCount=1).
		[Fact]
		public void DecodeAppearanceLenient_RuntimeBearing_RecoversSameMorphs()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var postStart = read.Offset + read.Length;

			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var cleanMorphs));
			// Clean post-IIR: [u32 0][u32 5][u32 2][5-byte record1][table + terminator]. Keep everything after the
			// 12-byte header + 5-byte record1 (the morph table + terminator) unchanged.
			var tableAndTerminator = body[(postStart + 12 + 5)..];

			var runtime = new byte[40];
			new Random(1).NextBytes(runtime);
			runtime[0] = 0xFF; // ensure bytes[12..17] can't be mistaken for the empty record1 (strict must decline)
			var runtimePost = new FaolanStream()
				.WriteUInt32(0u).WriteUInt32((uint)runtime.Length).WriteUInt32(1u)
				.WriteArray(runtime).WriteArray(tableAndTerminator).ToArray();

			var runtimeBody = new byte[postStart + runtimePost.Length];
			Array.Copy(body, runtimeBody, postStart);
			runtimePost.CopyTo(runtimeBody, postStart);
			BinaryPrimitives.WriteUInt32BigEndian(runtimeBody, (uint)(runtimeBody.Length - 4));

			Assert.False(NpcSpawnAppearanceCodec.TryDecodeAppearance(runtimeBody, out _));
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(runtimeBody, out var lenientMorphs));
			Assert.Equal(cleanMorphs.Count, lenientMorphs.Count);
			// Both lift the identical appearance table (BuildPostIir is the canonical fresh form for both).
			Assert.Equal(NpcSpawnAppearanceCodec.BuildPostIir(cleanMorphs), NpcSpawnAppearanceCodec.BuildPostIir(lenientMorphs));
		}

		// A clean-shape body decodes identically under the lenient path (firstRecordLen == 5, record1 == the empty
		// record) - the lenient decode is a strict superset, so it never regresses the 582 clean captures.
		[Fact]
		public void DecodeAppearanceLenient_CleanShape_MatchesStrict()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var strict));
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(body, out var lenient));
			Assert.Equal(NpcSpawnAppearanceCodec.BuildPostIir(strict), NpcSpawnAppearanceCodec.BuildPostIir(lenient));
		}

		// Animals (the 41-byte minimal post-IIR) carry no table under either decoder.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther")]
		public void DecodeAppearanceLenient_Animal_ReturnsFalse(string golden)
		{
			Assert.False(NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(Golden.Bytes(golden), out var morphs));
			Assert.Empty(morphs);
		}
	}
}
