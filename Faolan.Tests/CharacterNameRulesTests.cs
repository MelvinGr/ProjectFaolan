using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Progression;
using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The character-name rules and the framing of the client list that feeds them.
	//
	// There is deliberately no test that asserts a specific rude word is refused. The whole point of storing the
	// list hashed is that the words are not in this repo, and a test naming one would put it back. What is testable
	// without them is the mechanism: a term hashes to a stored row, a name containing that term is refused, and one
	// that does not is allowed. The tests below build their own synthetic list for exactly that reason.
	public class CharacterNameRulesTests
	{
		private static (IReadOnlySet<string> Hashes, IReadOnlySet<int> Lengths) FilterOf(params string[] terms)
		{
			return (terms.Select(CharacterNameRules.Hash).ToHashSet(StringComparer.Ordinal), terms.Select(t => t.Length).ToHashSet());
		}

		private static readonly (IReadOnlySet<string>, IReadOnlySet<int>) NoFilter =
			(new HashSet<string>(), new HashSet<int>());

		[Theory]
		[InlineData("Con", CharacterNameRules.Verdict.TooShort)] // 3
		[InlineData("", CharacterNameRules.Verdict.TooShort)]
		[InlineData("Conan", CharacterNameRules.Verdict.Ok)]
		[InlineData("NecroL80", CharacterNameRules.Verdict.Ok)]
		[InlineData("Conanofcimmeria", CharacterNameRules.Verdict.Ok)] // 15
		[InlineData("Conanofcimmerii", CharacterNameRules.Verdict.Ok)] // 15
		[InlineData("Conanofcimmeriaa", CharacterNameRules.Verdict.Ok)] // 16, the stated ceiling
		[InlineData("Conanofcimmeriaaa", CharacterNameRules.Verdict.TooLong)] // 17
		public void Length_MatchesTheRuleTheClientStates(string name, CharacterNameRules.Verdict expected)
		{
			// "The length of the name must be between 4 and 16 characters." - LDB 211711[2780].
			Assert.Equal(expected, CharacterNameRules.Check(name, NoFilter.Item1, NoFilter.Item2));
		}

		[Fact]
		public void AForbiddenTerm_IsCaughtAnywhereInTheName_AndCaseFolded()
		{
			var filter = FilterOf("badword");

			Assert.Equal(CharacterNameRules.Verdict.Forbidden, CharacterNameRules.Check("badword", filter.Hashes, filter.Lengths));
			// The substring rule is what makes the list mean anything - an exact-match filter is defeated by one letter.
			Assert.Equal(CharacterNameRules.Verdict.Forbidden, CharacterNameRules.Check("xbadwordx", filter.Hashes, filter.Lengths));
			Assert.Equal(CharacterNameRules.Verdict.Forbidden, CharacterNameRules.Check("BadWord", filter.Hashes, filter.Lengths));
			Assert.Equal(CharacterNameRules.Verdict.Ok, CharacterNameRules.Check("badwor", filter.Hashes, filter.Lengths));
			Assert.Equal(CharacterNameRules.Verdict.Ok, CharacterNameRules.Check("Conan", filter.Hashes, filter.Lengths));
		}

		[Fact]
		public void AShortTermMatchesOnlyTheWholeName()
		{
			// Below CharacterNameRules.SubstringMinLength a term must BE the name. Matching a four-letter term as a
			// substring refuses `Assassin` and `Ambassador`, which the client itself ships as content - see the
			// calibration in the class remarks and `--report name-filter-check`.
			var filter = FilterOf("hack");

			Assert.Equal(CharacterNameRules.Verdict.Forbidden, CharacterNameRules.Check("hack", filter.Hashes, filter.Lengths));
			Assert.Equal(CharacterNameRules.Verdict.Forbidden, CharacterNameRules.Check("HACK", filter.Hashes, filter.Lengths));
			Assert.Equal(CharacterNameRules.Verdict.Ok, CharacterNameRules.Check("Shackleton", filter.Hashes, filter.Lengths));
		}

		[Fact]
		public void ATermExactlyAtTheFloorStillMatchesAnywhere()
		{
			var filter = FilterOf(new string('q', CharacterNameRules.SubstringMinLength));

			Assert.Equal(CharacterNameRules.Verdict.Forbidden,
				CharacterNameRules.Check("ab" + new string('q', CharacterNameRules.SubstringMinLength) + "cd", filter.Hashes, filter.Lengths));
		}

		[Fact]
		public void LengthIsCheckedBeforeContent_SoAShortForbiddenNameReadsAsTooShort()
		{
			// The client's own order: it refuses on length locally and never sends the name at all.
			var filter = FilterOf("bad");
			Assert.Equal(CharacterNameRules.Verdict.TooShort, CharacterNameRules.Check("bad", filter.Hashes, filter.Lengths));
		}

		[Fact]
		public void OnlyTermsThatCouldBeASubstringOfANameAreStored()
		{
			Assert.True(CharacterNameRules.IsUsableTerm("bad"));
			Assert.True(CharacterNameRules.IsUsableTerm("Sechzehnzeichen1")); // 16 - a name can be exactly this
			Assert.False(CharacterNameRules.IsUsableTerm("ab")); // matches almost every name
			Assert.False(CharacterNameRules.IsUsableTerm("siebzehnzeichenxx")); // 17 - longer than any name
			Assert.False(CharacterNameRules.IsUsableTerm("two words")); // a name is one token
			Assert.False(CharacterNameRules.IsUsableTerm("with-dash"));
			Assert.False(CharacterNameRules.IsUsableTerm("  "));
		}

		[Fact]
		public void Hash_IsTheLowercasedTerm_SoTheImporterAndTheCheckAgree()
		{
			Assert.Equal(CharacterNameRules.Hash("Conan"), CharacterNameRules.Hash("conan"));
			Assert.NotEqual(CharacterNameRules.Hash("conan"), CharacterNameRules.Hash("conano"));
			Assert.Equal(64, CharacterNameRules.Hash("conan").Length);
		}

		// ---- the client record's framing -------------------------------------------------------------------

		private static byte[] SynthesizedList(params string[] terms)
		{
			var s = new List<byte>();
			s.AddRange(BitConverter.GetBytes(RdbNameFilterList.Type));
			s.AddRange(BitConverter.GetBytes(0u));
			s.AddRange(BitConverter.GetBytes(1u));
			s.AddRange(BitConverter.GetBytes((uint)terms.Length));
			foreach (var t in terms)
			{
				var utf8 = Encoding.UTF8.GetBytes(t);
				s.AddRange(BitConverter.GetBytes((uint)utf8.Length));
				s.AddRange(utf8);
			}

			return s.ToArray();
		}

		[Fact]
		public void NameFilterList_ParsesTheCountThenLengthPrefixedUtf8()
		{
			// The real record is 1,819 entries of exactly this shape - see RdbNameFilterList for where the framing
			// came from. Multi-byte UTF-8 is the case that matters: the length is in bytes, not characters.
			Assert.Equal(["alpha", "팍", "beta"], RdbNameFilterList.Parse(SynthesizedList("alpha", "팍", "beta")));
		}

		[Fact]
		public void NameFilterList_ThrowsWhenTheChainDisagreesWithTheStatedCount()
		{
			// A reader that silently returns half a list makes an empty filter look like a clean one.
			var truncated = SynthesizedList("alpha", "beta")[..^2];
			Assert.Throws<InvalidOperationException>(() => RdbNameFilterList.Parse(truncated));

			var body = SynthesizedList("alpha");
			BitConverter.GetBytes(9u).CopyTo(body, 12);
			Assert.Throws<InvalidOperationException>(() => RdbNameFilterList.Parse(body));
		}
	}
}
