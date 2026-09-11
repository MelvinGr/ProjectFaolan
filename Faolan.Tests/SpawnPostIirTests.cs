using System.Numerics;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the canonical minimal NPC post-IIR stream (the freshly-spawned, no-runtime-state form) against real
	// capture bodies, and pins the decoded header grammar. The SpawnNpcCrocodile golden is one of the 23 beach NPCs
	// that carry exactly this minimal form.
	public class SpawnPostIirTests
	{
		[Fact]
		public void MinimalNpc_HasTheDecodedHeaderGrammar()
		{
			var post = Rebuild(SpawnPostIirs.Npc(SpawnPostIirs.MovementModeWalk));

			Assert.Equal(0u, post.ReadU32Be(0)); // the head-word list's count, 0 - not a constant
			Assert.Equal(5u, post.ReadU32Be(4)); // the first record: the empty move-task slot + the buff list's frame
			Assert.Equal(SpawnPostIirs.MovementModeWalk, post.ReadU32Be(8)); // the third word is the movement mode
			Assert.Equal(ListFrame.Empty, post.ReadU32Be(post.Length - 4)); // the closing pair list, empty
		}

		[Fact]
		public void MinimalNpc_ReproducesTheCapturedMinimalScaffold()
		{
			var captured = CapturedPostIir("SpawnNpcCrocodile");

			Assert.Equal(SpawnPostIirs.MinimalNpcLength, captured.Length);
			// The mode is passed explicitly rather than defaulted: it is this Crocodile's own movement mode (1), and
			// the builder's fallback (2) is what we send for a type that has none - not a claim about any capture.
			Assert.Equal(captured, Rebuild(SpawnPostIirs.Npc(SpawnPostIirs.MovementModeWalk)));
		}

		[Fact]
		public void MinimalNpc_ReproducesThePantherScaffold()
		{
			Assert.Equal(CapturedPostIir("SpawnNpcMinimalPanther"), Rebuild(SpawnPostIirs.Npc(SpawnPostIirs.MovementModeWalk)));
		}

		// No head words. That leading u32 is the head-word list's count, not a constant - it is 0 on 11,627 captured
		// streams and 1 on the 5 that carry a word, so reading it as "constant 0 (the player spawn has 2)" is wrong on
		// both halves.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void DecodeEncode_IsByteIdentical(string golden)
		{
			Assert.True(SpawnBodyReader.TryParse(Golden.Bytes(golden), out var spawn));

			Assert.Empty(spawn.Post.HeadWords);
			Assert.Equal(CapturedPostIir(golden), Rebuild(spawn.Post));
		}

		// TryReadNoMorphWord2 reads the fresh word2 off a no-morph body (minimal or runtime-bearing) and
		// declines an appearance-bearing body (which builds from morph rows). The animal minimal golden is word2==1;
		// Casilda (a morph table) is declined.
		[Fact]
		public void TryReadNoMorphWord2_ReadsMinimal_DeclinesAppearance()
		{
			Assert.True(NpcSpawnAppearance.TryReadNoMorphWord2(Golden.Bytes("SpawnNpcCrocodile"), out var word2));
			Assert.Equal(SpawnPostIirs.MovementModeWalk, word2);

			Assert.False(NpcSpawnAppearance.TryReadNoMorphWord2(Golden.Bytes("SpawnNpcCasilda"), out _));
		}

		// A runtime-bearing no-morph stream (a first record carrying the entity's current move task, no morph table)
		// still yields its header word2 - the key to building the runtime tail from columns.
		//
		// 40 random bytes behind a `firstRecordLen` that says so would parse only for a reader that takes word2 off a
		// fixed offset without walking the stream - a fixture shaped like its parser, which proves only that the file
		// agrees with itself. The slot is a MoveTaskNode, so this builds one.
		[Fact]
		public void TryReadNoMorphWord2_ReadsRuntimeBearingForm()
		{
			var animal = Golden.Bytes("SpawnNpcCrocodile");
			Assert.True(SpawnBodyReader.TryParse(animal, out var spawn));

			var post = SpawnPostIirs.Npc(SpawnPostIirs.MovementModeRun);
			post.MoveTask = MoveTaskNodes.Walk(new Vector3(1, 2, 3), new Vector3(4, 5, 6));
			spawn.Post = post;

			var body = new byte[4 + SimpleCharFullUpdateCodec.Build(spawn).Length];
			SimpleCharFullUpdateCodec.Build(spawn).CopyTo(body, 4);
			body.WriteU32Be(0, (uint)(body.Length - 4));

			Assert.True(NpcSpawnAppearance.TryReadNoMorphWord2(body, out var word2));
			Assert.Equal(SpawnPostIirs.MovementModeRun, word2);
		}

		// The captured post-IIR stream, sliced off behind the embedded CharacterIIR.
		private static byte[] CapturedPostIir(string golden)
		{
			var body = Golden.Bytes(golden);
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(body, out var read));
			return body[(read.Offset + read.Length)..];
		}

		private static byte[] Rebuild(SpawnPostIir post)
		{
			using var s = new FaolanStream();
			SpawnPostIirCodec.Write(s, post);
			return s.ToArray();
		}
	}
}
