using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Xunit;

namespace Faolan.Tests
{
	// #719: an item may only sit in a character's inventory if we know its HASH.
	//
	// Retail never sends a hash-less item in a snapshot - 0 of 8,479 inventory entries across every captured
	// `FullPlayerCharacterIIR` lack one - so a hash-less bag row is a state retail never reaches. Ours once could: a
	// claim redeem was allowed to persist an item whose hash had never been harvested, the world-entry snapshot then
	// DROPPED the row (it renders Hash != 0 only), and a per-world-entry live re-add papered over the resulting
	// invisible item - re-announcing "You received [X]" on every MAP CHANGE. Both the escape hatch and the re-add are
	// gone; this gate is what keeps them from coming back.
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
