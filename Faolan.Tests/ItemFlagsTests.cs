using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
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
