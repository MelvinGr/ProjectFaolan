using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Progression;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The group finder's activity -> destination catalog, read out of RDB 1000627 (#511).
	//
	// The claim under test is the KEYING, because that is the part a table cannot assert about itself: an ActivityId
	// is the record's 1-based sequence number, flat across the whole table rather than relative to the category the
	// client names alongside it. One capture decides it, and the two gates below are the two halves of that - the
	// captured activity must land on the captured playfield AND at the captured arrival, and the rival reading
	// (count within the category) must NOT.
	public class GroupFinderActivityCatalogTests
	{
		// `CategoryId=4;ActivityId=38` -> playfield 1092, arrival (112, 70, 39), from the one signup in the corpus.
		// [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json the 20:22 entry]
		private const uint CapturedActivityId = 38;
		private const uint CapturedMapId = 1092;

		[Fact]
		public void TheCapturedActivity_ResolvesToThePlayfieldAndArrivalRetailSent()
		{
			var activity = GroupFinderActivityCatalog.ActivityFor(CapturedActivityId);

			Assert.NotNull(activity);
			Assert.Equal(CapturedMapId, activity.MapId);

			// The arrival the captured N3TeleportIIR put the player at, to the precision the wire carried.
			Assert.Equal(112.0f, activity.Arrival.X, 1);
			Assert.Equal(70.0f, activity.Arrival.Y, 1);
			Assert.Equal(39.0f, activity.Arrival.Z, 1);
		}

		[Fact]
		public void TheRivalKeying_IsRefutedByTheSameCapture()
		{
			// ⭐ The control that makes the gate above evidence rather than a restatement. The table's trailing
			// 94-entry section invites reading the id as an index WITHIN the category the scry parameter names
			// (`CategoryId=4`), and counting activity 38 that way lands on record 65 - Oasis of Zaara (3204), a
			// landmark-addressed row with no coordinate at all. So the two readings genuinely disagree on the one
			// case we can check, and the capture picks the flat one.
			//
			// Pinned as "the catalog does not hold 3204 for the captured id": if someone re-keys the catalog by
			// category later, this fails rather than silently entering the wrong dungeon.
			var activity = GroupFinderActivityCatalog.ActivityFor(CapturedActivityId);

			Assert.NotNull(activity);
			Assert.NotEqual(3204u, activity.MapId);
		}

		[Fact]
		public async Task EveryCatalogDestination_IsAPlayfieldWeActuallyHold()
		{
			// A destination that resolves to no Map row would teleport a player into a map the server cannot load -
			// and the ids come from the CLIENT's table, so this is a real cross-check between two independent
			// sources rather than a tautology.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			foreach (var activity in GroupFinderActivityCatalog.Activities.Values)
			{
				var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == activity.MapId, ct);

				Assert.True(map != null, $"activity {activity.ActivityId} names playfield {activity.MapId} ({activity.MapName}), which is not in Maps");
				Assert.Equal(activity.MapName, map!.Name);
			}
		}

		[Fact]
		public void TheCatalogHoldsOnlyCoordinateCarryingActivities()
		{
			// The table's other rows address their destination by a LANDMARK NAME (`Frost_Swamp_Zone_In`,
			// `Imirian_Ravine_Zone_In`, `Oasis_of_Zaara_Zone_In`, `StartPlace`) or are 24-man raid-finder rows, and
			// neither carries a coordinate. Listing one here would mean inventing an arrival, so the catalog's own
			// invariant is that every row it does hold states a real destination.
			Assert.NotEmpty(GroupFinderActivityCatalog.Activities);

			foreach (var activity in GroupFinderActivityCatalog.Activities.Values)
			{
				Assert.NotEqual(0u, activity.MapId);
				Assert.True(activity.Arrival != System.Numerics.Vector3.Zero,
					$"activity {activity.ActivityId} carries a zero arrival - a landmark-addressed row does not belong in this catalog");
			}

			// Keyed by the id, so a duplicate row would silently shadow one of the two.
			Assert.Equal(GroupFinderActivityCatalog.Activities.Count, GroupFinderActivityCatalog.Activities.Values.Select(a => a.ActivityId).Distinct().Count());
		}
	}
}
