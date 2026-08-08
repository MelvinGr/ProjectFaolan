using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
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
	// value there) but about what reaches the WIRE. Every held item must resolve to a well-formed token.
	public class InventoryItemHashTests
	{
		[Fact]
		public async Task EveryHeldItem_ResolvesToAWellFormedWireToken()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var held = (from ci in db.CharacterInventory
					join i in db.Items on ci.ItemId equals i.Id
					select new { ci.CharacterId, ci.Container, ci.Slot, i.Id, i.Name, i.Hash })
				.ToList();

			var offenders = held
				.Where(o => !AcgHash.IsWellFormed(AcgHash.ForWire(o.Id, o.Hash)))
				.Take(20)
				.ToList();

			Assert.True(offenders.Count == 0,
				"a character is holding an item whose wire token the client would refuse:\n  "
				+ string.Join("\n  ", offenders.Select(o =>
					$"character {o.CharacterId} {o.Container}[{o.Slot}] -> item {o.Id} '{o.Name}' (harvested 0x{o.Hash:X8}, wire 0x{AcgHash.ForWire(o.Id, o.Hash):X8})")));
		}

		// A row pointing at a template that is not in Items at all is still a real defect - that one has no
		// identity to mint FROM, and the snapshot skips it, so the item is invisible with nothing to explain it.
		[Fact]
		public async Task NoInventoryRow_PointsAtAMissingItemTemplate()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var itemIds = db.Items.Select(i => i.Id).ToHashSet();
			var offenders = db.CharacterInventory
				.Where(ci => ci.ItemId != null)
				.Select(ci => new { ci.CharacterId, ci.Container, ci.Slot, ci.ItemId })
				.ToList()
				.Where(ci => !itemIds.Contains(ci.ItemId!.Value))
				.Take(20)
				.ToList();

			Assert.True(offenders.Count == 0,
				"a character is holding a template with no Items row:\n  "
				+ string.Join("\n  ", offenders.Select(o => $"character {o.CharacterId} {o.Container}[{o.Slot}] -> item {o.ItemId}")));
		}
	}
}
