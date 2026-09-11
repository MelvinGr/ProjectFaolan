using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// SimpleDynelVisualChangeIIR (0xE7F4F8F8) - the visual/collision state flip, decoded off the client's own reader
	// (`SimpleDynelVisualChangeIIR_c::ReadSubClass` = FUN_007a6910) rather than off what happened to be captured
	//.
	//
	// The tail is discriminated by a u32 kind, and the previous model split the record one byte early - `[u32
	// word0][u8 state]` where the wire has `[u8 0][u32 kind]` - which is why the leading word read as a constant zero
	// and everything after the "state" had to be carried as an opaque `Trailer`. The two shapes that "could not be
	// one grammar" (four zero bytes vs a length-prefixed name) are kinds 5 and 0 of the same switch.
	//
	// One captured body per kind the corpus contains, byte-exact through Parse -> Build. Sizes for the kinds the
	// corpus does not contain (1, 6, 7, 10, 11, 12) come from the same switch, so they are implemented rather than
	// left to throw; the round-trip below covers the seven that can be evidenced.
	public class SimpleDynelVisualChangeCodecTests
	{
		// (kind, captured body including its carrier length word, source)
		public static TheoryData<uint, string> CapturedBodies => new TheoryData<uint, string>
			{
				{ 0, "00000020e7f4f8f80000c878000139660000000000000d6f626a6563745f636c6f736564" }, // Barbarian-create #261
				{ 2, "00000029e7f4f8f80000c8780002c1ce0000000002004c46220000002200000000000000000000000000000000" }, // Hakotep_hox_login_gameplay0 #76
				{ 3, "00000015e7f4f8f80000c879000003b6000000000300000000" }, // DemoL80_..._randomActions #6265
				{ 4, "00000019e7f4f8f80000c879000076a60000000004002f830c000f696e" }, // Hakotep_hox_login_gameplay0 #81
				{ 5, "00000015e7f4f8f80000c74800000b980000000005000300b3" }, // Barbarian-create #261
				{ 8, "00000015e7f4f8f80000c87800000bf4000000000840000000" }, // Hoxxling_..._gameplay2 #223
				{ 9, "00000015e7f4f8f80000c87800000bf4000000000940000000" } // Hoxxling_..._gameplay2 #170
			};

		[Theory, MemberData(nameof(CapturedBodies))]
		public void CapturedBody_ParsesToItsKind_AndReEncodesByteExact(uint kind, string capturedHex)
		{
			var record = CapturedRecord.Bytes(capturedHex); // the carrier's [u32 len] is the sender's (#530 rule 2)

			var parsed = SimpleDynelVisualChangeCodec.Parse(record);

			Assert.Equal(kind, parsed.Kind);
			Assert.Equal(record, SimpleDynelVisualChangeCodec.Build(parsed));
		}

		// The two shapes that look like separate grammars. They differ only in the kind word: kind 0
		// takes a name, kind 5 takes one word - and both are read by the same switch.
		[Fact]
		public void TheNamedAndTheWordShapes_AreOneGrammar()
		{
			var named = SimpleDynelVisualChangeCodec.Parse(Convert.FromHexString("e7f4f8f80000c878000139660000000000000d6f626a6563745f636c6f736564"));
			var flip = SimpleDynelVisualChangeCodec.Parse(Convert.FromHexString("e7f4f8f80000c74800000b980000000005000300b3"));

			Assert.Equal("object_closed", named.Name);
			Assert.Empty(named.Words);

			Assert.Null(flip.Name);
			Assert.Equal([0x000300B3u], flip.Words);
		}

		// The two forms production sends: the collision drop (the Casilda chain) and the loot-window open.
		[Fact]
		public void TheProductionForms_BuildTheirCapturedShapes()
		{
			var flip = SimpleDynelVisualChangeCodec.Parse(SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetKdTree(EntityClass.SimpleDynel, 0x00013966, 0)));
			Assert.Equal(EntityClass.SimpleDynel, flip.TypeTag);
			Assert.Equal((uint)DynelVisualChange.SetKdTree, flip.Kind);
			Assert.Equal([0u], flip.Words);

			var open = SimpleDynelVisualChangeCodec.Parse(SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetBaseAnim(EntityClass.LootBag, 0x00000190, "Open")));
			Assert.Equal((uint)DynelVisualChange.SetBaseAnim, open.Kind);
			Assert.Equal("Open", open.Name);
		}

		// The harvest sequence, byte-exact against the capture it was decoded from -
		// `xxx_mage-necromancer_login_play2_tortage_city_first-quests`, the quarry Stone Brick 0x6AF3. Every record
		// of it, in the order retail sends them, built from what the object's DB row holds: the mesh pair
		// (GeometryAssetId 23748 / GeometryAssetType 1010030), the collision (Kdtree 168444 - which is that mesh
		// manifest's own `<CollisionMesh2 file>`), and CarsGroup 5.
		//
		// The two runs are the harvest and the regrowth, and the difference between them is the whole point: the
		// first clears the mesh and the collision, the second sets them back to the brick's own.
		[Fact]
		public void TheHarvestSequence_MatchesTheCapturedBrick()
		{
			const uint Dynel = EntityClass.SimpleDynel;
			const uint Brick = 0x00006AF3;

			// f4247 18:43:16.131 - the harvest opens with a 2.0 s fade-out.
			Assert.Equal("e7f4f8f80000c87800006af3000000000940000000", Hex(SimpleDynelVisualChanges.FadeOut(Dynel, Brick, 2.0f)));

			// f4276 18:43:18.181 - the fade has run: draw nothing (id 0, still the meshindexfile type), fade back in.
			Assert.Equal("e7f4f8f80000c87800006af3000000000400000000000f696e", Hex(SimpleDynelVisualChanges.ChangeMesh(Dynel, Brick, 0, 1010030)));
			Assert.Equal("e7f4f8f80000c87800006af3000000000840000000", Hex(SimpleDynelVisualChanges.FadeIn(Dynel, Brick, 2.0f)));

			// f4360 18:43:20.229 - the brick is invisible, so its collision goes, alongside its CarsGroup.
			Assert.Equal("f98e10b30000c87800006af3000000041a00000005", Convert.ToHexStringLower(StatCodec.Build(StatIirs.ForWorldObject(Dynel, Brick, CharacterStatId.CarsGroup, 5))));
			Assert.Equal("e7f4f8f80000c87800006af3000000000500000000", Hex(SimpleDynelVisualChanges.SetKdTree(Dynel, Brick, 0)));

			// f4495 18:43:30.468 - ten seconds later the same sequence runs the other way and the brick grows back.
			Assert.Equal("e7f4f8f80000c87800006af3000000000940000000", Hex(SimpleDynelVisualChanges.FadeOut(Dynel, Brick, 2.0f)));
			Assert.Equal("e7f4f8f80000c87800006af3000000000400005cc4000f696e", Hex(SimpleDynelVisualChanges.ChangeMesh(Dynel, Brick, 23748, 1010030)));
			Assert.Equal("e7f4f8f80000c87800006af30000000005000291fc", Hex(SimpleDynelVisualChanges.SetKdTree(Dynel, Brick, 168444)));
		}

		// The particle puff over the harvested brick (f4249). Not built by the server - the effect id is per-object
		// content no column holds - but the form is decoded, so the codec can state it: attractor 0
		// means "not attached to a bone", which is why this one carries a position, and that position is the brick's
		// own (its spawn puts it at 915.92 / 171.04 / 844.66).
		[Fact]
		public void TheHarvestParticleEffect_MatchesTheCapturedRecord()
		{
			var effect = SimpleDynelVisualChanges.StartParticleEffect(EntityClass.SimpleDynel, 0x00006AF3, 3667224, 0, 0f, new Vector3(912.4328f, 171.06451f, 844.13379f));

			Assert.Equal("e7f4f8f80000c87800006af300000000020037f518000000000000000044641bb3432b108444530890", Hex(effect));
		}

		private static string Hex(SimpleDynelVisualChangeIIR value)
		{
			return Convert.ToHexStringLower(SimpleDynelVisualChangeCodec.Build(value));
		}

		// Strict: a kind outside the client's switch is a decode failure, and so is a body whose
		// payload does not match the kind's stated size - the whole point of the switch is that the size is known.
		[Fact]
		public void AnUnknownKind_OrAShortPayload_Throws()
		{
			// kind 13 - past the client's switch.
			Assert.Throws<CodecParseException>(() => SimpleDynelVisualChangeCodec.Parse(Convert.FromHexString("e7f4f8f80000c8780000396600000000" + "0d" + "00000000")));

			// kind 4 declares two words; give it one.
			Assert.Throws<CodecParseException>(() => SimpleDynelVisualChangeCodec.Parse(Convert.FromHexString("e7f4f8f80000c8780000396600000000" + "04" + "00000000")));
		}
	}
}
