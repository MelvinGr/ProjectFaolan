using System;
using System.Buffers.Binary;
using Faolan.Core.Network.Codecs;
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

			Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(0, 4))); // word0 == 0 (player has 2)
			Assert.Equal((uint)(post.Length - 36), BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(4, 4))); // self-length
			Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(8, 4))); // single record group
			Assert.Equal(0x03F1u, BinaryPrimitives.ReadUInt32BigEndian(post.AsSpan(post.Length - 4, 4))); // final 0x3F1 marker
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

		// #243: TryReadMinimalRecordCount recognises the exact 41-byte minimal form and reports its recordCount - the
		// signal that a no-morph spawn's fresh post-IIR is BuildMinimalPostIir(rc). The animal golden carries rc==1.
		[Fact]
		public void TryReadMinimalRecordCount_AnimalMinimalForm_ReturnsCount1()
		{
			Assert.True(NpcSpawnScaffold.TryReadMinimalRecordCount(Golden.Bytes("SpawnNpcCrocodile"), out var rc));
			Assert.Equal(NpcSpawnScaffold.MinimalRecordCount, rc);
		}

		// A synthetic no-morph HUMANOID minimal form (recordCount==2) is recognised and reports 2; an appearance-bearing
		// body (Casilda's morph table) is NOT the minimal form, so it is declined.
		[Fact]
		public void TryReadMinimalRecordCount_HumanoidMinimalForm_ReturnsCount2_AndDeclinesAppearance()
		{
			var animal = Golden.Bytes("SpawnNpcCrocodile");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(animal, out var read));
			var postStart = read.Offset + read.Length;

			// Rebuild the animal body with the humanoid minimal post-IIR (recordCount==2) and fixed body length.
			var humanoidPost = NpcSpawnScaffold.BuildMinimalPostIir(NpcSpawnScaffold.MinimalHumanoidRecordCount);
			var body = new byte[postStart + humanoidPost.Length];
			Array.Copy(animal, body, postStart);
			humanoidPost.CopyTo(body, postStart);
			BinaryPrimitives.WriteUInt32BigEndian(body, (uint)(body.Length - 4));

			Assert.True(NpcSpawnScaffold.TryReadMinimalRecordCount(body, out var rc));
			Assert.Equal(NpcSpawnScaffold.MinimalHumanoidRecordCount, rc);

			Assert.False(NpcSpawnScaffold.TryReadMinimalRecordCount(Golden.Bytes("SpawnNpcCasilda"), out _));
		}

		[Fact]
		public void WithMinimalPostIir_OnAlreadyMinimalBody_IsAByteIdenticalNoOp()
		{
			var body = Golden.Bytes("SpawnNpcCrocodile"); // post-IIR already the minimal 41B form

			Assert.Equal(body, NpcSpawnScaffold.WithMinimalPostIir(body));
		}

		[Fact]
		public void WithMinimalPostIir_OnRuntimeStateBody_StripsToMinimalAndFixesBodyLength()
		{
			var body = Golden.Bytes("SpawnNpcCasilda"); // a 250B runtime-state post-IIR
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var before));

			var stripped = NpcSpawnScaffold.WithMinimalPostIir(body);

			// The body shrank by exactly (oldPost - minimalPost), the IIR is untouched, and the post-IIR is minimal.
			var oldPostLen = body.Length - (before.Offset + before.Length);
			Assert.Equal(body.Length - (oldPostLen - NpcSpawnScaffold.MinimalPostIirLength), stripped.Length);

			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(stripped, out var after));
			Assert.Equal(before.Offset, after.Offset);
			Assert.Equal(before.Length, after.Length); // IIR unchanged
			Assert.Equal(NpcSpawnScaffold.BuildMinimalPostIir(), stripped[(after.Offset + after.Length)..]);

			// Inner body-length field (@0) == total body length - 4 (the captured convention).
			Assert.Equal((uint)(stripped.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(stripped.AsSpan(0, 4)));
		}
	}
}
