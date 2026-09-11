using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// An item in a character's inventory must be one the world-entry snapshot can render.
	//
	// The failure this guards is real and was shipped: a claim redeem persisted an item whose hash had never been
	// harvested, the world-entry snapshot then dropped the row (it rendered Hash != 0 only), and a per-world-entry
	// live re-add papered over the resulting invisible item - re-announcing "You received [X]" on every map change.
	//
	// The invariant is about what reaches the wire, not about Items.Hash: the token carries no information and the
	// client validates nothing, so AcgHash.ForWire mints one for the 232 templates no capture ever showed entering an
	// inventory, and those items are perfectly holdable. Items.Hash is a provenance record where 0 is a normal value,
	// so "no inventory row may reference a hash-0 item" would fail the moment a claim or an AH purchase legitimately
	// delivers one. Every item must resolve to a well-formed token instead.
	//
	// And it is asked of the template population, not of held instances. `db.ItemInstances` is excluded from the seed
	// with the rest of the player state - 404 rows in `faolan.db`, zero in `faolan.sql` - so a gate reading it would
	// assert "no offender among no rows". Asking `Items` is not a weaker question but a strictly stronger one: it
	// covers every template that could be held rather than the handful some captured character happened to be
	// carrying, and `Items` is seeded, so the gate can actually fail. The held-instance form, which needs the real database, is
	// `--report inventory-token-check`.
	public class InventoryItemHashTests
	{
		[Fact]
		public async Task EveryItemTemplate_ResolvesToAWellFormedWireToken()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var items = await db.Items.AsNoTracking().Select(i => new
			{
				i.Id,
				i.Name,
				i.Hash
			}).ToListAsync(ct);

			Assert.NotEmpty(items); // the population is real - otherwise this gate is the vacuous kind it replaced

			var offenders = items
				.Where(o => !AcgHash.IsWellFormed(AcgHash.ForWire(o.Id, o.Hash)))
				.Take(20)
				.ToList();

			Assert.True(offenders.Count == 0,
				"an item template mints a wire token the client would refuse:\n  "
				+ string.Join("\n  ", offenders.Select(o =>
					$"item {o.Id} '{o.Name}' (harvested 0x{o.Hash:X8}, wire 0x{AcgHash.ForWire(o.Id, o.Hash):X8})")));
		}
	}
}
