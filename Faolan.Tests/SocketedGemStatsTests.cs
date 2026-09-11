using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// A gem socketed into a worn item grants its stats, and it grants them through the ordinary item-stat path.
	//
	// A socket carries no stat value in either wire form - the persistent `slot_map` and the live
	// `DispatchAddToItemSlot` both name the gem by template and nothing else - so the gem's effect is its own
	// template's modifier list, and folding it in is a matter of returning the gem alongside the item it sits in.
	public class SocketedGemStatsTests
	{
		private const uint CharacterId = 311;
		private const uint GlovesTemplate = 47001;
		private const uint RingTemplate = 47002;
		private const uint GemTemplate = 47010;
		private const uint OtherGemTemplate = 47011;

		// Magic Damage - the stat the two Azurite gems in the shipped DB actually carry, so the test exercises the
		// same shape rather than an invented id.
		private const CharacterStatId Stat = CharacterStatId.MagicDamage;

		[Fact]
		public async Task ASocketedGem_CountsTowardsTheWearersStats_OncePerSocket()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-gem-stats-{Guid.NewGuid():N}.db");
			File.Copy(TestDb.GameDatabase, path);
			try
			{
				var options = TestDb.Options(path);
				await using (var ctx = new DatabaseContext(options))
				{
					var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
					var account = await ctx.Accounts.Select(a => a.Id).FirstAsync(TestContext.Current.CancellationToken);
					ctx.Characters.Add(new Character
					{
						Id = CharacterId, Name = "Gemtest", Class = ConanClass.Necromancer, Level = 20, Race = Race.Stygian,
						Sex = Sex.Female, RealmId = realm, AccountId = account
					});
					ctx.Items.Add(new Item { Id = GlovesTemplate, Name = "Plain Gloves" });
					ctx.Items.Add(new Item { Id = RingTemplate, Name = "Plain Ring" });
					ctx.Items.Add(new Item { Id = GemTemplate, Name = "Cabochon Azurite" });
					ctx.Items.Add(new Item { Id = OtherGemTemplate, Name = "A Bag Gem" });
					ctx.ItemStatModifiers.Add(new ItemStatModifier { ItemId = GemTemplate, StatId = Stat, Amount = 1500 });
					ctx.ItemStatModifiers.Add(new ItemStatModifier { ItemId = OtherGemTemplate, StatId = Stat, Amount = 9900 });

					// Worn gloves with two of the same gem in two sockets, a worn ring with none, and - the control -
					// an unworn bag item carrying a gem that must not count.
					var gloves = new ItemInstance { Id = 90001, OwnerCharacterId = CharacterId, Container = ContainerKind.Equipped, Slot = (int)CharacterEquipmentSlot.Hands, TemplateId = GlovesTemplate };
					var ring = new ItemInstance { Id = 90002, OwnerCharacterId = CharacterId, Container = ContainerKind.Equipped, Slot = (int)CharacterEquipmentSlot.LeftRing, TemplateId = RingTemplate };
					var inBag = new ItemInstance { Id = 90003, OwnerCharacterId = CharacterId, Container = ContainerKind.Bag, Slot = 4, TemplateId = GlovesTemplate };
					ctx.ItemInstances.AddRange(gloves, ring, inBag);
					ctx.ItemInstanceSockets.AddRange(
						new ItemInstanceSocket { ItemInstanceId = gloves.Id, SocketIndex = 0, GemItemId = GemTemplate },
						new ItemInstanceSocket { ItemInstanceId = gloves.Id, SocketIndex = 1, GemItemId = GemTemplate },
						new ItemInstanceSocket { ItemInstanceId = inBag.Id, SocketIndex = 0, GemItemId = OtherGemTemplate });
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var repo = new DatabaseRepository(new TestDb.ContextFactory(options));
				var worn = await repo.GetEquippedStatItems(CharacterId, WeaponSet.Primary);

				// Two worn items plus the two gems in the gloves. The bag item's gem is absent - it is not worn, and
				// neither is the item holding it.
				Assert.Equal(4, worn.Length);
				Assert.Equal(2, worn.Count(i => i.Id == GemTemplate));
				Assert.DoesNotContain(worn, i => i.Id == OtherGemTemplate);

				// Once per socket, not once per distinct gem: two of the same gem is that bonus twice. De-duplicating
				// here would halve the bonus, exactly as it does for the worn items themselves.
				var total = worn.SelectMany(i => i.StatModifiers).Where(m => m.StatId == Stat).Sum(m => m.Amount);
				Assert.Equal(3000, total);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}
	}
}
