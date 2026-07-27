using System;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the canonical minimal NPC post-IIR scaffold (the freshly-spawned, no-runtime-state form) against a
	// real capture body, and pins the dissected header grammar (word0=0, word1=len-36, word2=count). The
	// SpawnNpcCrocodile golden is one of the 23 beach NPCs that carry exactly this minimal form.
	public class NpcSpawnScaffoldTests
	{
		[Fact]
		public void BuildMinimalPostIir_HasTheDissectedHeaderGrammar()
		{
			var post = NpcSpawnScaffold.BuildMinimalPostIir();

			Assert.Equal(0u, post.ReadU32Be(0)); // word0 == 0 (player has 2)
			Assert.Equal((uint)(post.Length - 36), post.ReadU32Be(4)); // self-length
			Assert.Equal(1u, post.ReadU32Be(8)); // single record group
			Assert.Equal(0x03F1u, post.ReadU32Be(post.Length - 4)); // final 0x3F1 marker
		}

		[Fact]
		public void BuildMinimalPostIir_ReproducesTheCapturedMinimalScaffold()
		{
			var body = Golden.Bytes("SpawnNpcCrocodile");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));

			var capturedPostIir = body[(read.Offset + read.Length)..];

			Assert.Equal(NpcSpawnScaffold.MinimalPostIirLength, capturedPostIir.Length);
			Assert.Equal(capturedPostIir, NpcSpawnScaffold.BuildMinimalPostIir());
		}

		// #243: TryReadNoMorphRecordCount reads the fresh recordCount off a no-morph body (minimal OR runtime-bearing)
		// and declines an appearance-bearing body (which builds from morph rows). The animal minimal golden is rc==1;
		// Casilda (a morph table) is declined.
		[Fact]
		public void TryReadNoMorphRecordCount_ReadsMinimal_DeclinesAppearance()
		{
			Assert.True(NpcSpawnScaffold.TryReadNoMorphRecordCount(Golden.Bytes("SpawnNpcCrocodile"), out var rc));
			Assert.Equal(NpcSpawnScaffold.MinimalRecordCount, rc);

			Assert.False(NpcSpawnScaffold.TryReadNoMorphRecordCount(Golden.Bytes("SpawnNpcCasilda"), out _));
		}

		// A RUNTIME-BEARING no-morph body (a large runtime record1, no morph table) still yields its header
		// recordCount - the key to building the runtime tail from columns.
		[Fact]
		public void TryReadNoMorphRecordCount_ReadsRuntimeBearingForm()
		{
			var animal = Golden.Bytes("SpawnNpcCrocodile");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(animal, out var read));
			var postStart = read.Offset + read.Length;

			// Synthesize a runtime-bearing no-morph post-IIR: [0][frl=40][rc=2] + a 40-byte runtime record + terminators.
			var runtime = new byte[40];
			new Random(2).NextBytes(runtime);
			var terminator = new byte[] { 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x03, 0xF1, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x03, 0xF1 };
			var post = new FaolanStream().WriteUInt32(0u).WriteUInt32(runtime.Length).WriteUInt32(2u).WriteArray(runtime).WriteArray(terminator).ToArray();
			var body = new byte[postStart + post.Length];
			Array.Copy(animal, body, postStart);
			post.CopyTo(body, postStart);
			body.WriteU32Be(0, (uint)(body.Length - 4));

			Assert.True(NpcSpawnScaffold.TryReadNoMorphRecordCount(body, out var rc));
			Assert.Equal(2u, rc);
		}
	}
}
