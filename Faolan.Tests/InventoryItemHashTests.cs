using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #719/#723: an item in a character's inventory must be one the world-entry snapshot can RENDER.
	//
	// The failure this guards is real and was shipped: a claim redeem persisted an item whose hash had never been
	// harvested, the world-entry snapshot then DROPPED the row (it rendered Hash != 0 only), and a per-world-entry
	// live re-add papered over the resulting invisible item - re-announcing "You received [X]" on every MAP CHANGE.
	//
	// ⚠️ What changed with #723: the gate used to be "no inventory row may reference a hash-0 item", because a
	// token could only be HARVESTED and an item without one was undeliverable. That is no longer true - the token
	// carries no information and the client validates nothing, so AcgHash.ForWire mints one for the 232 templates
	// no capture ever showed entering an inventory, and those items are now perfectly holdable. Keeping the old
	// assertion would fail the moment a claim or an AH purchase legitimately delivers one.
	//
	// So the invariant moved down a level: it is no longer about Items.Hash (a provenance record, 0 is a normal
	// value there) but about what reaches the WIRE. Every item must resolve to a well-formed token.
	//
	// ⚠️ And it is asked of the TEMPLATE population, not of held instances (#849). Both gates here used to read
	// `db.ItemInstances` out of `TestDb.GameDatabase`, and the seed excludes that table with the rest of the player
	// state: 404 rows in `faolan.db`, zero in `faolan.sql`. They were asserting "no offender among no rows". Asking
	// `Items` instead is not a weaker question but a strictly stronger one - it covers every template that COULD be
	// held rather than the handful some captured character happened to be carrying, and `Items` is seeded, so the
	// gate can actually fail. The held-instance form, which needs the real database, is
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
