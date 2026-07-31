using System;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// SimpleDynelVisualChangeIIR (0xE7F4F8F8) - the visual/collision state flip, decoded off the CLIENT's own reader
	// (`SimpleDynelVisualChangeIIR_c::ReadSubClass` = FUN_007a6910) rather than off what happened to be captured
	// (#535).
	//
	// The tail is discriminated by a u32 KIND, and the previous model split the record one byte early - `[u32
	// word0][u8 state]` where the wire has `[u8 0][u32 kind]` - which is why the leading word read as a constant zero
	// and everything after the "state" had to be carried as an opaque `Trailer`. The two shapes that "could not be
	// one grammar" (four zero bytes vs a length-prefixed name) are kinds 5 and 0 of the same switch.
	//
	// One captured body PER KIND the corpus contains, byte-exact through Parse -> Build. Sizes for the kinds the
	// corpus does NOT contain (1, 6, 7, 10, 11, 12) come from the same switch, so they are implemented rather than
	// left to throw; the round-trip below covers the seven that can be evidenced.
	public class SimpleDynelVisualChangeCodecTests
	{
		// (kind, captured body INCLUDING its carrier length word, source)
		public static TheoryData<uint, string> CapturedBodies =>
			new()
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
			var record = Convert.FromHexString(capturedHex)[4..]; // the carrier's [u32 len] is the sender's (#530 rule 2)

			var parsed = SimpleDynelVisualChangeCodec.Parse(record);

			Assert.Equal(kind, parsed.Kind);
			Assert.Equal(record, SimpleDynelVisualChangeCodec.Build(parsed));
		}

		// The two shapes the old comment said could not be one grammar. They differ ONLY in the kind word: kind 0
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

		// The two convenience forms production sends, still byte-identical to what they built before the decode: the
		// collision flip (#267's Casilda chain) and the loot-window open.
		[Fact]
		public void TheProductionForms_BuildTheirCapturedShapes()
		{
			var flip = SimpleDynelVisualChangeCodec.Parse(SimpleDynelVisualChangeCodec.Build(0x00013966, 5));
			Assert.Equal(EntityClass.SimpleDynel, flip.TypeTag);
			Assert.Equal(5u, flip.Kind);
			Assert.Equal([0u], flip.Words);

			var open = SimpleDynelVisualChangeCodec.Parse(SimpleDynelVisualChangeCodec.Build(EntityClass.LootBag, 0x00000190, "Open"));
			Assert.Equal(SimpleDynelVisualChangeCodec.NamedKind, open.Kind);
			Assert.Equal("Open", open.Name);
		}

		// Strict (#530 rule 4): a kind outside the client's switch is a decode failure, and so is a body whose
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
