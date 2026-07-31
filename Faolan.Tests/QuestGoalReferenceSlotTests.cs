using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// What `QuestGoalDefinition.Extra0..Extra6` - the goal record's seven "reserved" words - actually hold (#546).
	//
	// The seven are a lead word plus THREE optional `(marker, identity)` REFERENCE slots at [1..2] / [3..4] / [5..6],
	// and the marker is a TYPE TAG on the identity:
	//
	//   * `50003` - a real LDB category (`nc_trade.tradename`, named by the client's own descriptor table), and the
	//     identity is an LDB refId. All three goals that carry one hold the goal's OWN `TrailingRefId` verbatim,
	//     which is the Rosetta stone for the whole slot: it says the slot is another spelling of the goal's target.
	//   * `70099` - the client's "unset id" SENTINEL. `FUN_006dd970`, the `GameCode/Criteria` evaluator, guards every
	//     id compare with `!= 0x111d3` and logs "user criteria failed" when a criterion's id equals it. So
	//     `(70099, token)` means "not an LDB reference" and the identity is the object's 4-character ACGHash name.
	//
	// WHAT THEY REFER TO is what these gates pin, because it is not visible in any single row:
	//   * slot [1..2] is the goal's TARGET - the item/object the goal is about, the same object `TrailingCategory` /
	//     `TrailingRefId` names.
	//   * slot [5..6] is its NPC - every `TrailingCategory` 51000 goal carries one, and the only ITEM goals that do
	//     are all twelve `Separator` 20 (Deliver) goals, i.e. exactly the goals that need a recipient.
	//   * the token is a FUNCTION of the referenced object, not a per-entry random: it is a bijection with the item on
	//     the 50200 goals and with the NPC on the 51000 goals.
	//
	// ⚠️ The slots FLOAT - goal 11252 uses [1] and [3] - so these gates are written over "whichever slots are
	// occupied", never over a fixed position. Still open: the lead word [0].
	//
	// A token -> object map is CLOSED as not recoverable (#547): the client never dereferences a token, so the
	// twelve Deliver recipients are read out of the objective TEXT by design rather than as a stopgap. See
	// QuestObjectiveGoalCodec's Reserved comment for the evidence.
	public class QuestGoalReferenceSlotTests
	{
		private const uint AcgHashMarker = 70099;
		private const uint ItemCategory = 50200;
		private const uint DeliverSeparator = 20;
		private const uint LdbRefMarker = 50003;
		private const uint NpcCategory = 51000;

		// The client's ACGHash validity rule (`FUN_005d7190`): four bytes, each `[0-9A-Z_]`. It checks nothing else -
		// there is no value to compare against - so this is the whole test for "is this word a token".
		private static bool IsAcgHash(uint word)
		{
			for (var shift = 0; shift < 32; shift += 8)
			{
				var c = (char)((word >> shift) & 0xFF);
				if (!(c is >= '0' and <= '9' or >= 'A' and <= 'Z' or '_'))
					return false;
			}

			return true;
		}

		private static IEnumerable<(uint Marker, uint Identity)> Slots(QuestGoalDefinition g)
		{
			return new[] { (g.Extra1, g.Extra2), (g.Extra3, g.Extra4), (g.Extra5, g.Extra6) }.Where(s => s.Item1 != 0);
		}

		private static async Task<List<QuestGoalDefinition>> Goals()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));
			return await db.QuestGoalDefinitions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
		}

		// The marker is a CLOSED type tag, and each type implies the shape of the identity beside it. If a re-harvest
		// ever introduces a third marker this fails rather than silently widening the "two types" claim above.
		[Fact]
		public async Task Markers_AreOneOfTwoTypes_AndTypeTheIdentity()
		{
			var bad = (from g in await Goals()
				from slot in Slots(g)
				where slot.Marker != AcgHashMarker && slot.Marker != LdbRefMarker
					|| slot.Marker == AcgHashMarker && !IsAcgHash(slot.Identity)
					|| slot.Marker == LdbRefMarker && IsAcgHash(slot.Identity)
				select $"goal {g.GoalId}: marker {slot.Marker} identity 0x{slot.Identity:X8}").ToList();

			Assert.True(bad.Count == 0, "a reserved slot's marker types its identity - 70099 an ACGHash token, 50003 an LDB refId:\n  " + string.Join("\n  ", bad));
		}

		// The Rosetta stone: where the goal spells the reference as an LDB ref instead of a token, it is the goal's
		// OWN trailing reference. This is what makes slot [1..2] "the goal's target" rather than an unknown id.
		[Fact]
		public async Task LdbRefSlots_HoldTheGoalsOwnTrailingRefId()
		{
			var rows = (from g in await Goals()
				from slot in Slots(g)
				where slot.Marker == LdbRefMarker
				select (g.GoalId, slot.Identity, g.TrailingRefId)).ToList();

			Assert.NotEmpty(rows);
			var bad = rows.Where(r => r.Identity != r.TrailingRefId).Select(r => $"goal {r.GoalId}: slot {r.Identity} != TrailingRefId {r.TrailingRefId}").ToList();
			Assert.True(bad.Count == 0, "a 50003 slot restates the goal's own target reference:\n  " + string.Join("\n  ", bad));
		}

		// The token identifies the OBJECT, so it must agree wherever the same object is referenced and must not be
		// shared between two of them. A per-entry random token would fail both directions.
		[Theory]
		[InlineData(ItemCategory)]
		[InlineData(NpcCategory)]
		public async Task Tokens_AreABijectionWithTheReferencedObject(uint category)
		{
			// The item rides slot [1..2] and the NPC slot [5..6]; take whichever the goal's own category makes it.
			var pairs = (from g in await Goals()
				where g.TrailingCategory == category && g.TrailingRefId != 0
				let token = category == ItemCategory ? (g.Extra1 == AcgHashMarker ? g.Extra2 : 0) : (g.Extra5 == AcgHashMarker ? g.Extra6 : 0)
				where token != 0
				select (g.TrailingRefId, Token: token)).ToList();

			Assert.NotEmpty(pairs);
			var twoTokens = pairs.GroupBy(p => p.TrailingRefId).Where(x => x.Select(p => p.Token).Distinct().Count() > 1).Select(x => $"ref {x.Key}").ToList();
			var twoObjects = pairs.GroupBy(p => p.Token).Where(x => x.Select(p => p.TrailingRefId).Distinct().Count() > 1).Select(x => $"token 0x{x.Key:X8}").ToList();

			Assert.True(twoTokens.Count == 0, $"one object in category {category} carries two tokens: " + string.Join(", ", twoTokens));
			Assert.True(twoObjects.Count == 0, $"one token in category {category} names two objects: " + string.Join(", ", twoObjects));
		}

		// The NPC slot: mandatory on an NPC goal, and on an ITEM goal it appears only where a recipient is needed.
		// This is the whole evidence that slot [5..6] is the NPC rather than a second target.
		[Fact]
		public async Task NpcSlot_IsOnEveryNpcGoal_AndOnlyOnDeliverItemGoals()
		{
			var goals = await Goals();

			var npcGoals = goals.Where(g => g.TrailingCategory == NpcCategory).ToList();
			Assert.NotEmpty(npcGoals);
			var missing = npcGoals.Where(g => g.Extra5 != AcgHashMarker).Select(g => $"goal {g.GoalId}").ToList();
			Assert.True(missing.Count == 0, "every NPC goal names its NPC in slot [5..6]: " + string.Join(", ", missing));

			var itemGoalsWithNpc = goals.Where(g => g.TrailingCategory == ItemCategory && g.Extra5 == AcgHashMarker).ToList();
			Assert.NotEmpty(itemGoalsWithNpc);
			var notDeliver = itemGoalsWithNpc.Where(g => g.Separator != DeliverSeparator).Select(g => $"goal {g.GoalId} (separator {g.Separator})").ToList();
			Assert.True(notDeliver.Count == 0, "only a Deliver item goal carries an NPC - it is the recipient: " + string.Join(", ", notDeliver));
		}
	}
}
