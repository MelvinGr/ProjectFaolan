using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// A consumable's shared cooldown. The client's own template states it - `CoreLockSpellLineTemplate`, kind
	// 53124, key 39 the spell line and key 66 the duration - and the line groups by the resource restored, so health,
	// stamina and mana lock independently.
	//
	// The mechanism is `DispatchLockTemplate`, not a refusal: the client greys what it is told to lock and never
	// sends the second use. So the line membership is the load-bearing part, and it comes from the items themselves -
	// there is no line catalog. These gate the shipped rows the runtime resolves that membership from.
	public class ConsumableCooldownTests
	{
		[Fact]
		public async Task TheThreeRESOURCELines_AreDistinctAndPopulated()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var byLine = (await db.Items.AsNoTracking().Where(i => i.OnUseLockSpellLine != null).ToListAsync(ct))
				.GroupBy(i => i.OnUseLockSpellLine!.Value)
				.ToDictionary(g => g.Key, g => g.ToList());

			// health / stamina / mana, each with a real membership. If they collapsed to one line, a health potion would
			// lock mana - the specific wrongness the ticket rules out.
			foreach (var line in new uint[] { 80000, 80100, 80200 })
				Assert.True(byLine.GetValueOrDefault(line, []).Count >= 5, $"line {line} has {byLine.GetValueOrDefault(line, []).Count} member(s)");

			// No item is on two lines - it cannot be, one column - but the lines must not share members either, which
			// is what makes them independent.
			var members = byLine.SelectMany(l => l.Value.Select(i => i.Id)).ToList();
			Assert.Equal(members.Count, members.Distinct().Count());
		}

		[Fact]
		public async Task EveryLockedConsumable_HasBothAAndDurationANDSomethingToLock()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.Items.AsNoTracking().Where(i => i.OnUseLockSpellLine != null || i.OnUseLockSeconds != null).ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(rows);
			Assert.All(rows, i =>
			{
				// Both or neither: a line with no duration locks nothing, and a duration with no line has nothing to lock.
				Assert.NotNull(i.OnUseLockSpellLine);
				Assert.NotNull(i.OnUseLockSeconds);
				Assert.True(i.OnUseLockSeconds > 0);

				// And a lock is pointless on an item with no on-use effect: the cooldown exists to gate the effect.
				Assert.NotNull(i.OnUseSpell);
			});

			// The observed durations are 60 s (the potions/drinks) and 1800 s (a 30-minute group). A value outside a
			// plausible band would mean the key was read as something else - the unit itself is still inferred, which is
			// why this is a band rather than an equality.
			Assert.All(rows, i => Assert.InRange(i.OnUseLockSeconds!.Value, 1u, 86400u));
		}

		[Fact]
		public async Task TheDrinksTheTicketNAMED_CarryTheLinesItMeasured()
		{
			// The four the ticket dumped by hand, as the crib: if the element parse drifted, these would move.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			foreach (var (name, line) in new (string, uint)[] { ("Daggamalt", 80000), ("Devilsmead", 80000), ("Maulsmead", 80100), ("Moonspill", 80200) })
			{
				var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Name == name, ct);
				Assert.NotNull(item);
				Assert.Equal(line, item.OnUseLockSpellLine);
				Assert.Equal(60u, item.OnUseLockSeconds);
			}
		}
	}
}
