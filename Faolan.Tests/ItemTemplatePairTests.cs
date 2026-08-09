using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #752: `ProtoAcgItem` carries TWO templates with a key each, and they are the LOW and HIGH endpoints of a level
	// range - `AcgItemData_t::GetInterpolatedItem` takes both plus the entry's own level and works out where between
	// them this copy sits. We used to send `template2 = template1` for everything, which pins every item at its low
	// endpoint.
	//
	// Measured over 13,368 captured ProtoAcgItems (344 distinct pairs): 3 differ, and they differ in nothing but
	// level fields. So most items genuinely repeat the template and the emitters must keep doing that - the gate
	// here is that the pair is DERIVED from the row rather than assumed either way.
	public class ItemTemplatePairTests
	{
		private static ProtoDecryptionKey Key(uint seed)
		{
			return new ProtoDecryptionKey { Key1 = seed, Key2 = seed + 1, Key3 = seed + 2, Key4 = seed + 3 };
		}

		private static ProtoAcgItem IdentityOf(Item item)
		{
			return SnapshotItems.ListEntry(0, 1, 2, 1, item, item.Hash).Item!.ItemData!;
		}

		// The common case, and the one that must not regress: no second endpoint means the wire repeats the first.
		[Fact]
		public void AnItemWithNoPair_RepeatsItsOwnTemplateAndKey()
		{
			var item = new Item { Id = 4242, Name = "Plain", Key = Key(10) };
			var acg = IdentityOf(item);

			Assert.Equal(4242u, acg.Template1);
			Assert.Equal(4242u, acg.Template2);
			Assert.Equal(item.Key.Key1, acg.Key2.Key1);
			Assert.Equal(0u, acg.Template3);
		}

		// The pair travels WITH ITS OWN KEY. Sending template2 without the key that decrypts it would be worse than
		// flattening - the client would hold a template it cannot read - so the two are asserted together.
		[Fact]
		public void AnItemWithAPair_SendsBothTheSecondTemplateAndItsOwnKey()
		{
			var item = new Item
			{
				Id = 3976768,
				Name = "Cabochon Azurite",
				Key = Key(10),
				TemplateId2 = 3976769,
				PairKey = Key(500)
			};

			var acg = IdentityOf(item);

			Assert.Equal(3976768u, acg.Template1);
			Assert.Equal(3976769u, acg.Template2);
			Assert.Equal(500u, acg.Key2.Key1);

			// key3 == key1 on all 344 captured pairs, so it follows the FIRST key, not the second.
			Assert.Equal(item.Key.Key1, acg.Key3.Key1);
		}

		// A socket is stated twice on the wire - the `slot_map` block and the `ItemSlot<N>ACG1/ACG2` stat pair - and
		// the corpus pins the two AGREEING 898/898. ACG1/ACG2 are literally template1/template2, so a gem with a pair
		// has to put its real second endpoint in ACG2 or the two forms contradict each other.
		[Fact]
		public void ASocketedGemsStatPair_MatchesItsSlotMapBlock()
		{
			var gem = new Item { Id = 3976768, Name = "Cabochon Azurite", Key = Key(10), TemplateId2 = 3976769, PairKey = Key(500) };
			var host = new Item { Id = 220212, Name = "Fine Steelsilk Gloves", Key = Key(20) };
			var worn = new ItemInstance
			{
				Container = Core.Enums.ContainerKind.Equipped,
				Slot = 15,
				Count = 1,
				TemplateId = host.Id,
				Template = host,
				Sockets = { new ItemInstanceSocket { SocketIndex = 0, GemItemId = gem.Id, Gem = gem } }
			};

			var entry = SnapshotItems.ListEntry(worn, 1, 2).Item!;

			var block = Assert.Single(entry.SlotMaps).Item!;
			Assert.Equal(3976768u, block.Template1);
			Assert.Equal(3976769u, block.Template2);

			// ItemSlot1ACG1 / ItemSlot1ACG2 = 0x2A3 / 0x2A4 for socket 0.
			Assert.Equal(block.Template1, entry.StatLists.Single(s => s.Stat == 0x2A3).Value);
			Assert.Equal(block.Template2, entry.StatLists.Single(s => s.Stat == 0x2A4).Value);
		}

		// The shipped catalog. `TemplateId2` and `PairKey` are one fact in two columns - a second template we cannot
		// decrypt is not a pair we can send - so neither may exist without the other.
		[Fact]
		public async Task EveryShippedPair_CarriesItsOwnKey()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var pairs = await db.Items.AsNoTracking()
				.Where(i => i.TemplateId2 != null)
				.Select(i => new { i.Id, i.Name, i.TemplateId2, i.PairKey })
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(pairs);
			Assert.All(pairs, p => Assert.True(
				(p.PairKey.Key1 | p.PairKey.Key2 | p.PairKey.Key3 | p.PairKey.Key4) != 0,
				$"{p.Id} \"{p.Name}\" declares template2 {p.TemplateId2} with an all-zero PairKey - the client could not decrypt it"));

			// And the reverse: a key with no template would be an orphan the emitters never reach.
			var orphans = await db.Items
				.Where(i => i.TemplateId2 == null && (i.PairKey.Key1 != 0 || i.PairKey.Key2 != 0 || i.PairKey.Key3 != 0 || i.PairKey.Key4 != 0))
				.CountAsync(TestContext.Current.CancellationToken);
			Assert.Equal(0, orphans);
		}
	}
}
