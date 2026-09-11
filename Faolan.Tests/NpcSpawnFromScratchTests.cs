using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// NpcSpawnBuilder.BuildBody assembles the whole FA015807 record with no captured template (header +
	// transform-with-token + pre-IIR + CoreChar header + message + post-IIR). Driven from golden captures, decomposed
	// into the same inputs the live path (BuildLive) feeds it: the built record must parse back and carry the expected
	// post-IIR shape - the minimal form for an animal, the appearance/morph table for a humanoid.
	//
	// The decomposition goes through the codec now rather than through SpawnPacketBuilder's fixed Strie-layout
	// offsets, which only ever held for one captured shape.
	public class NpcSpawnFromScratchTests
	{
		[Fact]
		public void BuildFromScratch_Animal_ParsesAndCarriesMinimalPostIir()
		{
			var golden = SimpleCharFullUpdateCodec.Parse(Golden.Bytes("SpawnNpcCrocodile")[4..]);
			var built = SimpleCharFullUpdateCodec.Parse(BuildFromGolden(golden, out var morphs));

			Assert.Empty(morphs); // an animal carries no appearance table
			Assert.Equal(golden.Handle, built.Handle);
			// The rebuilt post-IIR carries the golden's own movement mode, because the builder reads it off the
			// message rather than taking it as a parameter - which is what makes the two copies agree.
			Assert.Equal(golden.Post.MovementMode, built.Post.MovementMode);
			Assert.Equal(PostIirBytes(SpawnPostIirs.Npc(golden.Post.MovementMode)), PostIirBytes(built.Post));
		}

		[Fact]
		public void BuildFromScratch_Humanoid_ParsesAndCarriesAppearanceTable()
		{
			var golden = SimpleCharFullUpdateCodec.Parse(Golden.Bytes("SpawnNpcCasilda")[4..]);
			var built = SimpleCharFullUpdateCodec.Parse(BuildFromGolden(golden, out var morphs));

			Assert.NotEmpty(morphs); // Casilda's appearance/morph table
			// the post-IIR is the appearance table generated from the decoded morphs (not the minimal form)
			Assert.Equal(PostIirBytes(SpawnPostIirs.Npc(golden.Post.MovementMode, morphs)), PostIirBytes(built.Post));
			// and the transform round-trips (position/rotation/token preserved through the build)
			Assert.Equal(golden.Position, built.Position);
			Assert.Equal(golden.Rotation, built.Rotation);
			Assert.Equal(golden.Playfield, built.Playfield);
		}

		// Decompose a golden capture into the inputs BuildBody takes (IIR header, transform, token, message, morphs)
		// and reassemble from scratch - the same decomposition BuildLive performs off the DB columns.
		private static byte[] BuildFromGolden(SimpleCharFullUpdateIIR golden, out List<AppearanceEntry> morphs)
		{
			morphs = golden.Post.Slot.Appearance.ToList();

			return NpcSpawnBuilder.BuildBody(golden.Handle, golden.CoreCharIirHeader, golden.Position, golden.Rotation, (uint)golden.Playfield, golden.Character, morphs);
		}

		private static byte[] PostIirBytes(SpawnPostIir post)
		{
			using var s = new FaolanStream();
			SpawnPostIirCodec.Write(s, post);
			return s.ToArray();
		}
	}
}
