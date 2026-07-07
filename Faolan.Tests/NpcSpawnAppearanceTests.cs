using System.Linq;
using Faolan.Core.Network.Codecs;
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

		// WithAppearancePostIir splices a from-rows appearance table back onto the body; fed the body's own decoded
		// morphs it is a byte-identical no-op (the build-side oracle, same shape as the IIR splice).
		[Fact]
		public void WithAppearancePostIir_FedItsOwnMorphs_IsByteIdentical()
		{
			var body = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var morphs));
			Assert.Equal(body, NpcSpawnAppearanceCodec.WithAppearancePostIir(body, morphs));
		}
	}
}
