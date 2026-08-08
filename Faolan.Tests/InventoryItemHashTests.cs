using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Xunit;

namespace Faolan.Tests
{
	// #719: an item may only sit in a character's inventory if we know its HASH.
	//
	// Retail never sends a hash-less item in a snapshot - 0 of 8,479 inventory entries across every captured
	// `FullPlayerCharacterIIR` lack one - so a hash-less bag row is a state retail never reaches. Ours could:
	// `GrantItemToBackpack(allowHashless: true)` persists a claim redeem whose hash was never harvested, and the
	// world-entry snapshot then DROPS the row (it only renders Hash != 0). The item was invisible after a relog,
	// which `RedeliverNonSnapshotBagItems` papered over by re-pushing it as a live add on every world entry - i.e.
	// re-announcing "You received [X]" on every MAP CHANGE.
	//
	// The catalogue itself may hold hash-less items and deliberately does (232 of them): a claim list or a shop
	// window carries the identity quad with no hash, and those rows are what let the Claims window render an item
	// by key. So the invariant is not "every Item has a hash" - it is "nothing hash-less reaches an inventory".
	public class InventoryItemHashTests
	{
		[Fact]
		public async Task NoInventoryRow_ReferencesAnItemWithoutAHash()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var offenders = (from ci in db.CharacterInventory
					join i in db.Items on ci.ItemId equals i.Id
					where i.Hash == 0
					select new { ci.CharacterId, ci.Container, ci.Slot, i.Id, i.Name })
				.Take(20)
				.ToList();

			Assert.True(offenders.Count == 0,
				"a character is holding an item we cannot render in the world-entry snapshot:\n  "
				+ string.Join("\n  ", offenders.Select(o => $"character {o.CharacterId} {o.Container}[{o.Slot}] -> item {o.Id} '{o.Name}' (Hash 0)")));
		}
	}
}
