using System.IO;
using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Rdb;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// `GameActionTooltip` is a transcription of the client's tooltip dispatch (#782), and its own artifact
	// `docs/re/gameaction-tooltip-dispatch.tsv` is the dump it was read out of. So the gate that is worth having is
	// that the two AGREE - a hand-edited alias that the disassembly does not support fails here.
	public class GameActionTooltipTests
	{
		private static readonly string DispatchTsv = TestPaths.Repo("docs", "re", "gameaction-tooltip-dispatch.tsv");
		private static readonly string TokensTsv = TestPaths.Repo("docs", "re", "gameaction-tooltip-tokens.tsv");

		[Fact]
		public void SentenceKinds_MatchTheDisassembly()
		{
			// The dump's `aliasSentence` column is the literal `MOV ECX,<id>` the branch issues before fetching its
			// category-170 string. A row whose alias is its OWN id is the identity case and carries no alias here.
			var fromDump = File.ReadAllLines(DispatchTsv)
				.Where(l => !l.StartsWith('#') && l.Contains('\t'))
				.Select(l => l.Split('\t'))
				.Where(c => c.Length >= 4 && uint.TryParse(c[0], out _) && uint.TryParse(c[3], out _))
				.Select(c => (Kind: uint.Parse(c[0]), Alias: uint.Parse(c[3])))
				.Where(x => x.Kind != x.Alias)
				.ToDictionary(x => x.Kind, x => x.Alias);

			Assert.NotEmpty(fromDump);
			Assert.Equal(fromDump.OrderBy(x => x.Key), GameActionTooltip.SentenceKinds.OrderBy(x => x.Key));
		}

		// ⭐ The branch set decides whether a missing sentence is OUR debt or the client's own behaviour, so it has to
		// stay in step with the same dump. Every row of the dispatch TSV is a branched kind by construction.
		[Fact]
		public void HasTooltipBranch_CoversEveryDispatchedKind()
		{
			var dumped = File.ReadAllLines(DispatchTsv)
				.Where(l => !l.StartsWith('#') && l.Contains('\t'))
				.Select(l => l.Split('\t')[0])
				.Where(c => uint.TryParse(c, out _))
				.Select(uint.Parse)
				.ToList();

			Assert.NotEmpty(dumped);
			foreach (var kind in dumped)
				Assert.True(GameActionTooltip.HasTooltipBranch(kind), $"{kind} reaches a tooltip branch in the dump but the table says it does not");

			// ...and the negative side, which is the one that carries the claim: `DPSHit` is 2,660 elements in the
			// spell catalog and retail draws no line for any of them.
			Assert.False(GameActionTooltip.HasTooltipBranch(53013));
			Assert.False(GameActionTooltip.HasTooltipBranch(53238));
		}

		// An identity lookup must stay an identity lookup: a kind not in the alias table is its own sentence key, and
		// nothing may quietly redirect it.
		[Theory]
		[InlineData(53045u, 53045u)] // Modify - the alias TARGET, never itself aliased
		[InlineData(53088u, 53088u)] // FilteredAreaFormula
		[InlineData(53220u, 53045u)] // InterpolatedModify draws itself as Modify
		[InlineData(53062u, 53054u)] // SpellLineStatModifier -> SpellStatModifier
		public void SentenceKind_ResolvesTheAlias(uint kind, uint expected)
		{
			Assert.Equal(expected, GameActionTooltip.SentenceKind(kind));
		}

		// ⭐ The rule at the head of FUN_0149aa30, which is what makes a `Hit` element's SENTENCE a function of its
		// VALUES rather than of its type.
		[Theory]
		[InlineData(0x1Bu, -500, "DamageHealth")]
		[InlineData(0x1Bu, 500, "HealHealth")]
		[InlineData(0x1Bu, 0, "HealHealth")] // the branch is `min < 0`, so zero takes the heal arm
		[InlineData(0x1F8u, -500, "DamageOtherStat")]
		[InlineData(0x1F8u, 500, "DamageOtherStat")]
		public void HitSentenceToken_IsStatAndSign(uint statId, int min, string expected)
		{
			Assert.Equal(expected, GameActionTooltip.HitSentenceToken(statId, min));
		}

		// The token names are only useful because `elf28(token)` is how they key LDB 170. The artifact records both,
		// so this is the cheap check that the hash we would look up is the one the dump was verified against - a
		// typo'd token silently resolves to nothing at runtime and looks like a missing string.
		[Fact]
		public void TokenIds_AreTheElf28OfTheirToken()
		{
			var rows = File.ReadAllLines(TokensTsv)
				.Where(l => !l.StartsWith('#') && l.Contains('\t'))
				.Select(l => l.Split('\t'))
				.Where(c => c.Length >= 4 && uint.TryParse(c[3], out _))
				.ToList();

			Assert.NotEmpty(rows);
			foreach (var c in rows)
				Assert.Equal(uint.Parse(c[3]), Elf28.Of(c[2]));
		}

		// The three sentences the hit renderer picks between must all be in that artifact - they are the tokens the
		// renderer names in code, and a rename on one side only would go unnoticed otherwise.
		[Fact]
		public void HitSentenceTokens_AreInTheRecoveredSet()
		{
			var tokens = File.ReadAllLines(TokensTsv)
				.Where(l => !l.StartsWith('#') && l.Contains('\t'))
				.Select(l => l.Split('\t'))
				.Where(c => c.Length >= 3)
				.Select(c => c[2])
				.ToHashSet();

			Assert.Contains("DamageHealth", tokens);
			Assert.Contains("HealHealth", tokens);
			Assert.Contains("DamageOtherStat", tokens);
		}
	}
}
