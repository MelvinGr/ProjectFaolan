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
	}
}
