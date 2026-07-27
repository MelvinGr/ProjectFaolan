using System;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The feat-name parser (Gitea #251). The client encodes tree / group / rank in the catalog NAME
	// ("Soldier: Optional Training: Thrown Weapons 5"), so these columns are only as good as this split.
	//
	// Pure by design: it takes strings, not the RDB, so it runs from a plain clone. The RDB read itself is covered by
	// `--report feat-catalog`, which needs the client install.
	public class RdbFeatImporterTests
	{
		[Theory]
		// Three-part: class, group, base + bare rank.
		[InlineData("Soldier: Optional Training: Thrown Weapons 5", "Soldier", "Optional Training", "Thrown Weapons", 5)]
		// Three-part with a PARENTHESISED rank - the other spelling the catalog uses.
		[InlineData("Guardian: Optional Training: 2HE (3)", "Guardian", "Optional Training", "2HE", 3)]
		// Two-part: the tree is the leading segment, no group.
		[InlineData("Core Training: Poison Magic Rating 6", "Core Training", null, "Poison Magic Rating", 6)]
		[InlineData("Equip: Silk Armor 1", "Equip", null, "Silk Armor", 1)]
		// Bare name: no tree, no group, rank still parsed off the end.
		[InlineData("Light Blade Training 1", null, null, "Light Blade Training", 1)]
		public void ParseName_SplitsTreeGroupBaseAndRank(string name, string? tree, string? group, string expectedBase, int rank)
		{
			var parsed = RdbFeatImporter.ParseName(name);

			Assert.Equal(tree, parsed.Tree);
			Assert.Equal(group, parsed.Group);
			Assert.Equal(expectedBase, parsed.BaseName);
			Assert.Equal(rank, parsed.Rank);
		}

		// A name the pattern does not fit must keep its FULL text in BaseName. The columns are a convenience over
		// Name, so a parse that does not apply has to lose nothing - the failure mode to avoid is a feat silently
		// reduced to a fragment of its own name.
		[Theory]
		[InlineData("Bard")]
		[InlineData("Rare Loot")]
		[InlineData("This is a detected trap.")]
		public void ParseName_KeepsTheWholeName_WhenNothingMatches(string name)
		{
			var parsed = RdbFeatImporter.ParseName(name);

			Assert.Null(parsed.Tree);
			Assert.Null(parsed.Group);
			Assert.Equal(name, parsed.BaseName);
			Assert.Equal(0, parsed.Rank);
		}

		// A trailing number that is part of the name, not a rank, would corrupt BaseName if the regex were greedy
		// about digits - "T9_10" has no whitespace before the digits, so it is not a rank suffix.
		[Fact]
		public void ParseName_DoesNotTakeEmbeddedDigitsAsARank()
		{
			var parsed = RdbFeatImporter.ParseName("Wood T9_10");

			Assert.Equal("Wood T9_10", parsed.BaseName);
			Assert.Equal(0, parsed.Rank);
		}

		// The RECORD's effect tail, against the real bytes of RDB 1000020 id 115338 ("Soldier: 1HB and 1HE 1") - the
		// two-effect record that proved the list is count-framed rather than a single fixed slot. Kept as inline hex
		// because it is one small record: a fixture file would need the client install to regenerate.
		private const string TwoEffectRecordHex =
			"54420F008AC2010040420F003DC70000030000000F00000017000000971B00004C000000020000001E000000050000100000000001000000" +
			"2A0100000000000058000000000000003600000001000000150000002100000016000000536F6C646965723A2031484220616E6420314845" +
			"2031020000000E000000D30B000035CF000000000000040000000000000001000000000000000200000009000000AA030000D0020000" +
			"35CF000000000000040000000000000001000000000000000200000009000000AC030000D0020000";

		[Fact]
		public void ParseEffects_ReadsTheCountFramedEffectList()
		{
			var effects = RdbFeatImporter.ParseEffects(Convert.FromHexString(TwoEffectRecordHex));

			// ListFrame(2) = 3027 in the tail, so exactly two elements - NOT one fixed slot, which is what the
			// single-effect records alone would have suggested.
			Assert.Equal(2, effects.Count);
			Assert.Equal((938u, 720u), effects[0]);
			Assert.Equal((940u, 720u), effects[1]);
		}

		// A record whose tail is not the effect shape must yield an EMPTY list rather than a garbage pair - the
		// importer writes what this returns straight into FeatEffect rows.
		[Fact]
		public void ParseEffects_YieldsNothing_WhenTheTailIsNotAnEffectList()
		{
			Assert.Empty(RdbFeatImporter.ParseEffects([0x54, 0x42, 0x0F, 0x00]));
			Assert.Empty(RdbFeatImporter.ParseEffects(new byte[64]));
		}

		// The wire-id -> template-id map (#467), built from a synthetic resource in the real layout: a 20-byte header
		// [type][id][1][count][version] then 48-byte entries whose first two words are the pair and whose 11th is the
		// base index. Synthetic rather than captured because the real resource is 316 KB.
		[Fact]
		public void ParseIdMap_ReadsThePairsAndTheBaseIndex()
		{
			var body = new byte[20 + (2 * 48)];
			BitConverter.GetBytes(1000042u).CopyTo(body, 0);
			BitConverter.GetBytes(1u).CopyTo(body, 4);
			BitConverter.GetBytes(1u).CopyTo(body, 8);
			BitConverter.GetBytes(2u).CopyTo(body, 12); // count
			BitConverter.GetBytes(2u).CopyTo(body, 16); // version
			BitConverter.GetBytes(100u).CopyTo(body, 20);
			BitConverter.GetBytes(7336u).CopyTo(body, 24);
			BitConverter.GetBytes(1u).CopyTo(body, 20 + 32); // word 8 = the ROOT marker
			BitConverter.GetBytes(1u).CopyTo(body, 20 + 40);
			BitConverter.GetBytes(1800u).CopyTo(body, 68);
			BitConverter.GetBytes(7353u).CopyTo(body, 72);
			BitConverter.GetBytes(18u).CopyTo(body, 68 + 40);

			var map = RdbFeatImporter.ParseIdMap(body);

			Assert.Equal(2, map.Count);
			// (wire, template, baseIndex, rootFlag) - 100 is "Core Soldier Knowledge", an archetype ROOT, so its word 8
			// is 1; 1800 "Barbarian" here is left 0 to prove the flag is read per-entry and not assumed.
			Assert.Equal((100u, 7336u, 1u, 1u), map[0]);
			Assert.Equal((1800u, 7353u, 18u, 0u), map[1]);

			// The wire id splits into (BaseIndex, Rank) as base*100 + rank - the same split the client's own loader
			// computes as its fallback. Both of these are rank 0, which is the FIRST rank, not "unranked".
			Assert.Equal(0u, map[0].Wire % 100);
			Assert.Equal(0u, map[1].Wire % 100);
		}

		// A blank slot must not become a 0 -> 0 row: the table is a lookup, and a zero key would collide.
		[Fact]
		public void ParseIdMap_SkipsBlankSlots()
		{
			var body = new byte[20 + 48];
			BitConverter.GetBytes(1000042u).CopyTo(body, 0);
			BitConverter.GetBytes(1u).CopyTo(body, 12);

			Assert.Empty(RdbFeatImporter.ParseIdMap(body));
		}

		// Wrong resource, or a count that overruns the body: yield nothing rather than read past the end.
		[Fact]
		public void ParseIdMap_RejectsAResourceThatIsNotTheFeatTable()
		{
			var wrongType = new byte[20 + 48];
			BitConverter.GetBytes(1000020u).CopyTo(wrongType, 0);
			BitConverter.GetBytes(1u).CopyTo(wrongType, 12);
			Assert.Empty(RdbFeatImporter.ParseIdMap(wrongType));

			var overrun = new byte[20 + 10];
			BitConverter.GetBytes(1000042u).CopyTo(overrun, 0);
			BitConverter.GetBytes(99u).CopyTo(overrun, 12);
			Assert.Empty(RdbFeatImporter.ParseIdMap(overrun));
		}

		// The PREREQUISITE edges (#251): columns 2/3/4 of the same 48-byte entry, zero where unused. Two feats, one
		// with a single prerequisite and one with three, so both the "skip the zeros" and the ordering are covered.
		[Fact]
		public void ParsePrereqs_ReadsUpToThreeEdgesPerFeat_InColumnOrder()
		{
			var body = new byte[20 + (2 * 48)];
			BitConverter.GetBytes(1000042u).CopyTo(body, 0);
			BitConverter.GetBytes(2u).CopyTo(body, 12);

			BitConverter.GetBytes(6001u).CopyTo(body, 20);      // feat
			BitConverter.GetBytes(6000u).CopyTo(body, 20 + 8);  // col2 - its only prerequisite

			BitConverter.GetBytes(300000u).CopyTo(body, 68);    // feat
			BitConverter.GetBytes(6001u).CopyTo(body, 68 + 8);  // col2
			BitConverter.GetBytes(70u).CopyTo(body, 68 + 12);   // col3
			BitConverter.GetBytes(80u).CopyTo(body, 68 + 16);   // col4

			var edges = RdbFeatImporter.ParsePrereqs(body);

			Assert.Equal(4, edges.Count);
			Assert.Equal((6001u, 0, 6000u), edges[0]);
			Assert.Equal((300000u, 0, 6001u), edges[1]);
			Assert.Equal((300000u, 1, 70u), edges[2]);
			Assert.Equal((300000u, 2, 80u), edges[3]);
		}

		// A feat with no prerequisites must produce NO rows - a 0 edge would claim "requires feat 0", which is a
		// dependency on a feat that does not exist.
		[Fact]
		public void ParsePrereqs_EmitsNothing_ForAFeatWithNoPrerequisites()
		{
			var body = new byte[20 + 48];
			BitConverter.GetBytes(1000042u).CopyTo(body, 0);
			BitConverter.GetBytes(1u).CopyTo(body, 12);
			BitConverter.GetBytes(6000u).CopyTo(body, 20);

			Assert.Empty(RdbFeatImporter.ParsePrereqs(body));
		}

		// The u16 record shape, against the real bytes of template 3187268 - the Hellfire feat Hakotep unlocks on tape.
		// This shape carries a name AND a description behind a [u16 nameLen][u16 descLen] pair; knowing only the u32
		// shape left 3,633 plaintext records looking nameless, and every feat the three captured characters hold was
		// among them. Getting this wrong does not fail loudly - it silently drops feats from the catalog.
		private const string HellfireRecordHex =
			"54420F0044A2300040420F003DC70000030000000F00000017000000881F00004C000000000000001E000000050000000000000001000000" +
			"2A01000000000000580000000000000036000000010000004F000000A03D3100150000002100000008004B0048656C6C66697265" +
			"52656475636573207468652063617374696E672074696D65206F662048656C6C66697265204272656174682C2062757420696E6372656173" +
			"6573207468652072652D7573652074696D652E020000000E000000D30B000046CF000000000000040000000000000001000000000000000200000009000000FDA7000026010000";

		[Fact]
		public void ReadNameAndDescription_ReadsTheU16ShapeWithItsTooltip()
		{
			var (name, description) = RdbFeatImporter.ReadNameAndDescription(Convert.FromHexString(HellfireRecordHex));

			Assert.Equal("Hellfire", name);
			Assert.Equal("Reduces the casting time of Hellfire Breath, but increases the re-use time.", description);
		}

		// The u32 shape still reads, and yields NO description - an empty string there means the record carried none,
		// not that the parse failed.
		[Fact]
		public void ReadNameAndDescription_StillReadsTheU32Shape()
		{
			var (name, description) = RdbFeatImporter.ReadNameAndDescription(Convert.FromHexString(TwoEffectRecordHex));

			Assert.Equal("Soldier: 1HB and 1HE 1", name);
			Assert.Equal("", description);
		}
	}
}
