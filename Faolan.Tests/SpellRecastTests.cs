using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #271: `Spell.RecastCentiseconds` is sourced from the spell record's own `SetSpellRecastTime` (53226) /
	// `SetSpellLineRecastTime` (53077) GameAction element - see docs/re/spell-recast-source.md.
	//
	// The gate is the RETAIL TOOLTIP, transcribed off screenshots of a level-80 necromancer before any of this was
	// decoded (the `spell_cooldowns_observed.tsv` readings). That is what makes it a gate rather than a mirror of the
	// importer: the expected numbers were written down by a human reading the live client, and the actual ones come
	// out of a decode that never saw them.
	//
	// The ZEROES carry as much weight as the values. `Pestilential Blast` and `Ice Strike` are two of the
	// necromancer's most-used spells and their tooltips read no recast at all; a decode that put a plausible number
	// on everything would pass on the four non-zero rows and fail here.
	public class SpellRecastTests
	{
		// (spell id, display name, the recast its retail tooltip shows, in seconds)
		public static TheoryData<uint, string, double> ObservedTooltips => new TheoryData<uint, string, double>
			{
				{ 3666380, "Chill (Rank 5)", 4 },
				{ 3666339, "Lifestrike (Rank 5)", 12 },
				{ 220979, "Life Leech", 30 },
				{ 223500, "Unimaginable Terror (Rank 2)", 45 },
				{ 144316, "Pestilential Blast (Rank 6)", 0 },
				{ 1202981, "Pestilential Blast (Rank 1)", 0 },
				{ 144315, "Ice Strike (Rank 6)", 0 },
				{ 1203611, "Ice Strike (Rank 1)", 0 }
			};

		[Theory]
		[MemberData(nameof(ObservedTooltips))]
		public async Task ShippedRecast_MatchesTheRetailTooltip(uint spellId, string name, double seconds)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var spell = await db.Spells.AsNoTracking().SingleAsync(s => s.Id == spellId, TestContext.Current.CancellationToken);

			Assert.Equal(name, spell.Name);
			Assert.Equal(seconds, spell.RecastCentiseconds / 100.0);
		}

		// A rank carrying no element of its own inherits the one its sibling arms on their shared GUI stacking line -
		// which is what `SetSpellLineRecastTime` MEANS, and the reason the column is filled per target rather than
		// per record. Rank 1 of `Unimaginable Terror` carries nothing; rank 2 arms 45 s on line 41023, and casting
		// either puts both on cooldown.
		[Fact]
		public async Task ALineScopedRecast_ReachesEveryRankOfTheLine()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var line = await db.Spells.AsNoTracking()
				.Where(s => s.GuiStackingLine == 41023)
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(line);
			Assert.All(line, s => Assert.Equal(4500u, s.RecastCentiseconds));
		}

		// The unit is the client's own - centiseconds, applied as `value * 0.01` s by the recast timer. 67 of the
		// 3,682 shipped values are not a whole number of seconds, which is the measurement that says a seconds
		// column was lossy rather than merely differently scaled.
		[Fact]
		public async Task TheColumnIsCentiseconds_NotSeconds()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var subSecond = await db.Spells.AsNoTracking()
				.CountAsync(s => s.RecastCentiseconds > 0 && s.RecastCentiseconds % 100 != 0, TestContext.Current.CancellationToken);

			Assert.True(subSecond > 50, $"expected the sub-second recasts a seconds column truncated; found {subSecond}");
		}
	}
}
