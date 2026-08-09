using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #756: the item entry's `Flags` word (stat 0) is the TEMPLATE's own property field 0, not a constant.
	//
	// It used to be a `flags = 1` default parameter that no caller ever passed - the third time that exact shape
	// shipped a wrong constant on this message, after `level = 1` on the same entry and the dead `level` on
	// GrantItemToBackpack. So the gates here assert the DERIVATION rather than a value: a golden that passes the
	// number in cannot notice when the emitter stops reading the column (see the agent memory
	// `self_referential_golden_tests`, and `unpassed_default_parameter_ships_a_wrong_value`).
	public class ItemFlagsTests
	{
		private const uint BoundFlag = 1u << 26; // "Character Bound" - the client's own tooltip test, vt+0xA0(0x04000000)
		private const uint FlagsStat = 0;

		private static Item Template(uint? flags)
		{
			return new Item { Id = 3908313, Name = "Cloak of Hugin", Level = 63, Flags = flags };
		}

		private static uint FlagsOf(System.Collections.Generic.IEnumerable<Faolan.Core.Protobuf.ProtoInventoryGameEntry.StatValue> stats)
		{
			return stats.Single(s => s.Stat == FlagsStat).Value;
		}

		// The snapshot entry and the live add must BOTH read the column, and must both TRACK it - asserting one fixed
		// value would still pass if the emitter went back to a constant that happened to match.
		[Theory]
		[InlineData(1u)]
		[InlineData(0x04000001u)] // bound on pickup
		[InlineData(0x0C000001u)]
		[InlineData(0u)] // a template that genuinely carries zero - 65 of them do
		public void BothEntryForms_SendTheTemplatesFlagsWord(uint flags)
		{
			var item = Template(flags);

			Assert.Equal(flags, FlagsOf(SnapshotItems.ListEntry(0, 1, 2, 1, item, item.Hash).Item!.StatLists));
			Assert.Equal(flags, FlagsOf(SnapshotItems.LiveAddEntry(1, 2, 1, item, item.Hash).StatLists));
		}

		// A NULL column means no RDB template was readable, NOT that the template said zero - which is exactly why
		// the column is nullable. Those fall back to 1 (what everything shipped before #756), and the fallback must
		// not be reachable from a real zero.
		[Fact]
		public void AnUnreadTemplate_FallsBackToOne_AndAGenuineZeroDoesNot()
		{
			Assert.Equal(1u, FlagsOf(SnapshotItems.ListEntry(0, 1, 2, 1, Template(null), 0).Item!.StatLists));
			Assert.Equal(0u, FlagsOf(SnapshotItems.ListEntry(0, 1, 2, 1, Template(0), 0).Item!.StatLists));
		}

		// The shipped catalog: the reader that produced these values must not have DRIFTED past the property table.
		//
		// Walking past the display-name string lands in the record's `[2][9]` region, where the list-framing words
		// `0x7E2`/`0xBD3` at three byte alignments read as plausible fields - that gave 200-odd templates a `Flags` of
		// 0xE2000000 / 0x0007E200 / 0x0BD30000 before `ItemTemplateProperties` was made to stop at the string. Every
		// one of those drifted values has bit 0 CLEAR while every real non-zero one has it SET (4,676 of 4,741), so
		// this is the cheap invariant that catches the drift coming back.
		[Fact]
		public async Task EveryShippedFlagsWord_IsZeroOrCarriesBitZero()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var drifted = await db.Items
				.Where(i => i.Flags != null && i.Flags != 0 && (i.Flags & 1) == 0)
				.Select(i => new { i.Id, i.Name, i.Flags })
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.True(drifted.Count == 0,
				"Items.Flags values with bit 0 clear - the property-table walk drifted past the name string:\n" +
				string.Join("\n", drifted.Select(d => $"  {d.Id} \"{d.Name}\" = 0x{d.Flags:x8}")));
		}

		// #756's per-instance half, both directions. The template's word is what a fresh copy carries; the bound bit
		// is added by the INSTANCE overload and by nothing else, because a caller holding no instance is describing an
		// item nobody owns.
		[Fact]
		public void TheBoundBitComesFromTheInstance_NotTheTemplate()
		{
			var template = Template(1);
			var worn = new ItemInstance { Container = ContainerKind.Equipped, Slot = 2, Count = 1, TemplateId = template.Id, Template = template, Bound = true };
			var carried = new ItemInstance { Container = ContainerKind.Bag, Slot = 8, Count = 1, TemplateId = template.Id, Template = template };

			Assert.Equal(0x04000001u, FlagsOf(SnapshotItems.ListEntry(worn, 1, 2).Item!.StatLists));
			Assert.Equal(0x00000001u, FlagsOf(SnapshotItems.ListEntry(carried, 1, 2).Item!.StatLists));
			Assert.Equal(0x04000001u, FlagsOf(SnapshotItems.LiveAddEntry(worn, 1, 2).StatLists));

			// ...and a bind-on-PICKUP template carries it with the instance flag CLEAR, because the bit is in its own
			// word. Nothing has to be stored for that half.
			var mintsBound = new ItemInstance { Container = ContainerKind.Bag, Slot = 3, Count = 1, TemplateId = 1, Template = Template(0x04000001) };
			Assert.Equal(0x04000001u, FlagsOf(SnapshotItems.ListEntry(mintsBound, 1, 2).Item!.StatLists));
			Assert.False(mintsBound.Bound);
		}

		// The bind message, byte-exact against retail's own bytes. This is the last packet of the equip sequence in
		// demol80-demonologist_login_gameplay_annotated_3 at 09:47:00.842, the one Melvin annotated "just eqpuied item,
		// it is now bound": equipped(0x65) container, character 0x110099BD, slot 2, stat 0, value 0x04000001.
		[Fact]
		public void TheBindMessage_IsByteExactAgainstTheCapture()
		{
			var body = SetItemStatCodec.Build(SetItemStats.Bound(ContainerKind.Equipped, 0x110099BD, 2, 1));

			Assert.Equal("00000065110099bd000000020000000004000001", Convert.ToHexStringLower(body));
		}

		// The two RULES, evaluated the way the client evaluates them. Table-driven off `ItemInfoView`'s own predicates
		// so a change to either derivation has to restate the client's rule rather than quietly widen it.
		[Theory]
		[InlineData(0u, null, 0u, false)] // nothing set
		[InlineData(0x20u, null, 0u, true)] // MoreFlags bit 5 alone
		[InlineData(0u, 4u, 5u, true)] // quality > 1 AND Can bit 2 - Cloak of Hugin's shape
		[InlineData(0u, 4u, 1u, false)] // quality high, Can bit 2 clear
		[InlineData(0u, 0u, 5u, false)] // Can bit 2 set, quality too low - Slave Manacles' shape
		[InlineData(0u, 2u, 521u, false)] // Daggamalt: quality 2 but Can = 0x209, bit 2 clear
		public void BindsOnEquip_IsTheClientsPredicate(uint moreFlags, uint? quality, uint can, bool expected)
		{
			var item = new Item
			{
				Id = 1,
				MoreFlags = moreFlags,
				Can = can,
				Quality = (ItemQuality?)quality
			};

			Assert.Equal(expected, item.BindsOnEquip);
		}

		[Theory]
		[InlineData(null, false)] // no readable template - must NOT read as bound (a lifted `!= 0` would say true)
		[InlineData(1u, false)]
		[InlineData(0x04000001u, true)]
		[InlineData(0x05000001u, true)] // Cloak of Hugin: bits 24 and 26
		public void BindsOnPickup_IsFlagsBitTwentySix(uint? flags, bool expected)
		{
			Assert.Equal(expected, Template(flags).BindsOnPickup);
		}

		// The point of the exercise: bind-on-pickup is not a separate flag, it is a template that mints items already
		// carrying the bound bit. If the harvest ever stops filling the column this drops to zero silently, and the
		// only visible symptom in-client would be that no item ever says "Character Bound" again.
		[Fact]
		public async Task TheCatalogCarriesBoundOnPickupTemplates()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var bound = await db.Items.CountAsync(i => i.Flags != null && (i.Flags & BoundFlag) != 0, TestContext.Current.CancellationToken);

			// A floor, not the exact 1,267 shipped today: a re-harvest against a fuller RDB may only ADD templates,
			// and pinning the count would make that a failure instead of progress.
			Assert.True(bound >= 1000, $"only {bound} templates mint bound - the Flags harvest looks broken");
		}
	}
}
