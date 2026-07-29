using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The PvP criminal system (#503). The annotated capture walks the whole mechanic - kill a player, become criminal
	// with murder points, get refused by an NPC, work again once the debuff drops - and these gate the parts of it the
	// server now models.
	public class CriminalStatusTests
	{
		// The identification rests on the shipped spell rows agreeing with the capture, so it is worth gating that they
		// still do rather than trusting a constant: the captured buff is spell 3827287, and if that row's name or
		// duration ever changes under a re-import, the constant is pointing at the wrong thing.
		[Fact]
		public async Task TheCriminalSpell_IsNamedCriminal_AndRunsTheMeasuredDuration()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var criminal = await db.Spells.FirstOrDefaultAsync(s => s.Id == CriminalStatus.CriminalSpellId, ct);
			Assert.True(criminal != null, $"spell {CriminalStatus.CriminalSpellId} is missing - the criminal debuff has no row");
			Assert.Equal("Criminal", criminal!.Name);

			// 300 000 ms, which is the 307 s the capture measured across the buff's re-asserts.
			Assert.Equal(CriminalStatus.CriminalSeconds * 1000u, criminal.DurationMs);

			var murderer = await db.Spells.FirstOrDefaultAsync(s => s.Id == CriminalStatus.MurdererSpellId, ct);
			Assert.True(murderer != null, $"spell {CriminalStatus.MurdererSpellId} is missing - the Murderer tier has no row");
			Assert.Equal("Murderer", murderer!.Name);
		}

		// The RULES are the client's own words, not ours. Both descriptions state the vendor and travel-NPC refusal,
		// and only the Murderer one adds the guards - which is the whole basis for treating them as two tiers, so a
		// re-import that emptied those strings would quietly remove the evidence.
		[Fact]
		public async Task TheSpellDescriptions_StateTheRefusalRules()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var criminal = await db.Spells.FirstAsync(s => s.Id == CriminalStatus.CriminalSpellId, ct);
			Assert.Contains("vendors will not trade with you", criminal.Description ?? "", StringComparison.OrdinalIgnoreCase);
			Assert.Contains("smugglers", criminal.Description ?? "", StringComparison.OrdinalIgnoreCase);

			var murderer = await db.Spells.FirstAsync(s => s.Id == CriminalStatus.MurdererSpellId, ct);
			Assert.Contains("city guards", murderer.Description ?? "", StringComparison.OrdinalIgnoreCase);
		}

		// A criminal is refused by normal vendors but not by smugglers - the exception named in the same sentence of
		// the description. The shipped data has to actually contain a smuggler for that exception to be reachable.
		[Fact]
		public async Task SmugglersExistToTradeWithCriminals()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var smugglers = await db.NpcTemplates.Where(t => t.Name.Contains("Smuggler")).CountAsync(ct);
			Assert.True(smugglers > 0, "no Smuggler NPC exists, so a criminal has nobody left to trade with");
		}

		[Theory]
		[InlineData("Smuggler", true)]
		[InlineData("Tortage Smuggler", true)]
		[InlineData("The Smuggler", true)]
		[InlineData("smuggler", true)] // matched case-insensitively
		[InlineData("Blacksmith", false)]
		[InlineData("Trader", false)]
		[InlineData(null, false)]
		public void OnlySmugglers_TradeWithCriminals(string? vendorName, bool trades)
		{
			Assert.Equal(trades, CriminalStatus.TradesWithCriminals(vendorName));
		}

		// IsCriminal is a question about TIME, not a stored bool - the debuff expires on its own, with nothing needing
		// to run to clear it. An expiry in the past must therefore read as not-criminal.
		[Fact]
		public void IsCriminal_TracksTheExpiry_NotAFlag()
		{
			var character = new Character { Name = "Tester" };
			Assert.False(character.IsCriminal); // null = never committed anything

			character.CriminalUntil = DateTime.UtcNow.AddSeconds(CriminalStatus.CriminalSeconds);
			Assert.True(character.IsCriminal);

			character.CriminalUntil = DateTime.UtcNow.AddSeconds(-1);
			Assert.False(character.IsCriminal); // lapsed, with nothing having had to tick
		}

		// The murder-point pool reaches the login snapshot on stat 0x449, which the client's LDB names "Murder Points".
		// It used to be carried as a fixed 0; the criminal system needs it to move.
		[Fact]
		public void MurderPoints_ReachTheSnapshotSheet()
		{
			var character = new Character { Name = "Killer", Level = 40, Sex = Sex.Male, MurderPoints = CriminalStatus.PointsPerPlayerKill };

			var sheet = SnapshotStatSheet.For(character, null, null);
			var value = sheet.First(p => p.Id == (uint)CharacterStatId.MurderPoints).Value;

			Assert.Equal(CriminalStatus.PointsPerPlayerKill, value);
			Assert.Equal(0x449u, (uint)CharacterStatId.MurderPoints);
		}
	}
}
