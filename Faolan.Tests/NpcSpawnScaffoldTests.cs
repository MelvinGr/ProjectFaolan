using System;
using System.Buffers.Binary;
using Faolan.GameServer.Spawn;
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
		public void BuildMinimalPostIir_ReproducesTheCapturedMinimalScaffold()
		{
			var body = Golden.Bytes("SpawnNpcCrocodile");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));

			var capturedPostIir = body[(read.Offset + read.Length)..];

			Assert.Equal(NpcSpawnScaffold.MinimalPostIirLength, capturedPostIir.Length);
			Assert.Equal(capturedPostIir, NpcSpawnScaffold.BuildMinimalPostIir());
		}

		[Fact]
		public void BuildMinimalPostIir_HasTheDissectedHeaderGrammar()
		{
			var post = NpcSpawnScaffold.BuildMinimalPostIir();

			Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(0, 4))); // word0 == 0 (player has 2)
			Assert.Equal((uint)(post.Length - 36), BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(4, 4))); // self-length
			Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(8, 4))); // single record group
			Assert.Equal(0x03F1u, BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(post.Length - 4, 4))); // final 0x3F1 marker
		}
	}
}
