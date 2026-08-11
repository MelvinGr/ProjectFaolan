using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #498: the DESTRUCTIBLE trailer's first two words are the object's `(current, max)` health pair, and the max is
	// what separates the three populations that share `DestructibleFullUpdateIIR` and nothing else.
	//
	// This is the census stated as a gate. It is not a round-trip - the codec's round-trip already passes with the
	// words anonymous, which is exactly the blind spot: a gate that only re-Builds bytes cannot tell a health pair
	// from two opaque u32. What makes the naming falsifiable is the SHAPE of the values across the shipped rows -
	// `current <= max` everywhere, `max` constant within a named population, and the tail words dead.
	//
	// The populations, and why each one is the thing it is named after:
	//
	//   1,000,000   Poitain's `Location Object - *` - UNBUILT guild-city plot markers. Poitain is one of the three
	//               booking zones the client's own help book names (LDB 50020/689), and the marker set is exactly the
	//               build menu: Keep, Walls, Towers, Gate, Tavern, Temple, Tradepost, Megalith, Profession, ...
	//   1,000       harvest nodes (#494), and the ONE band whose `current` varies - because the capture recorded them
	//               part-way through somebody's mining session.
	//   100M..300M  the Border Kingdoms battlekeep, BUILT: the tier is in the name (`Wall II` / `Keep III`) and in
	//               the health, +50M per tier with a +25M step per building class.
	public class DestructibleHealthTests
	{
		private const uint Destructible = (uint)IirTypes.DestructibleFullUpdateIIR;

		[Fact]
		public async Task EveryDestructible_CarriesAHealthPair_AndADeadThreeWordTail()
		{
			var rows = await LoadDestructibles();
			Assert.NotEmpty(rows);

			foreach (var row in rows)
			{
				var trailer = Trailer(row);
				Assert.Equal(5, trailer.Length);

				var current = trailer[WorldObjectGeometryColumns.HealthCurrentWord];
				var max = trailer[WorldObjectGeometryColumns.HealthMaxWord];

				// A health bar cannot exceed its own maximum, and a max of 0 would mean an object with no bar at all -
				// neither appears, which is the first thing that would break if the pair were something else.
				Assert.True(max > 0, $"WorldObject {row.Id} '{row.Name}' has max health 0");
				Assert.True(current <= max, $"WorldObject {row.Id} '{row.Name}' has current {current} > max {max}");

				// Words 2..4 are 0 on every captured destructible. Unnamed, but pinned - so a future capture that
				// fills one of them fails here rather than riding along unnoticed.
				Assert.Equal([0u, 0u, 0u], trailer[2..]);
			}
		}

		// The max is a CATEGORY, not a per-object number: every object sharing a name shares a max. That is what makes
		// `HarvestCatalog.NodeMaxHealth` a discriminator rather than a coincidence, and it is the claim that would fail
		// first if word 1 were something per-instance.
		//
		// ⭐ ONE name breaks it - two `Wagon`s in Tarantia Common District at 58,000 and 62,000 - and it is **not** an
		// unexplained exception (#798). It used to be recorded as one, on the belief that nothing in the columns
		// separated them. Something does: their `DisplayId`s are **278481 and 278482**, two different entries of LDB
		// category 55000, and both read `Wagon` in every one of the seven shipped languages (German `Wagen`, French
		// `Chariot`, Spanish `Carromato`, Russian `Повозка`, Polish `Wóz`, Korean `짐마차`).
		//
		// So they are two distinct display IDENTITIES wearing one text, and the max stays a per-template constant -
		// the string-vs-identity split #734 found on the nodes, here with the discriminating column named.
		//
		// ⚠️ The two columns are different things and that is what the old reading missed: `Name` is the wire's own
		// INTERNAL name (ProtoSimpleDynelIIR field 2, never localized - one object on map 1010 is called
		// `Blacksmithing Hammer TESTITEM` and displays as `Cotton Shrub`), while `DisplayId` is the localized
		// reference the player actually reads (field 3). Grouping by `Name` therefore merges identities on purpose;
		// it is the right grouping for "is the max a category?" and the wrong one for "are these the same object".
		//
		// The test asserts the explanation rather than pinning the exception: the split name's rows must differ in
		// `DisplayId`, and each `DisplayId` must carry ONE max - so a genuinely unexplained split (one DisplayId, two
		// maxima) fails here instead of being absorbed by the allowance.
		[Fact]
		public async Task MaxHealth_IsConstantWithinAName_AndTheTarantiaWagonsAreTwoNameIds()
		{
			var rows = await LoadDestructibles();

			var split = rows.GroupBy(r => r.Name)
				.Select(g => (Name: g.Key, Rows: g.ToList(), Maxima: g.Select(r => Trailer(r)[WorldObjectGeometryColumns.HealthMaxWord]).Distinct().Order().ToList()))
				.Where(x => x.Maxima.Count > 1)
				.ToList();

			Assert.Equal(["Wagon"], split.Select(x => x.Name));
			Assert.Equal([58_000u, 62_000u], split[0].Maxima);

			// The explanation: as many distinct name ids as there are maxima, so the max is still one per identity.
			Assert.Equal([278481u, 278482u], split[0].Rows.Select(r => r.DisplayId).Distinct().Order().ToList());
			Assert.All(split[0].Rows.GroupBy(r => r.DisplayId),
				g => Assert.Single(g.Select(r => Trailer(r)[WorldObjectGeometryColumns.HealthMaxWord]).Distinct()));
		}

		// The harvest half, stated the way the subsystem relies on it: a node is exactly the 1000-max band, and the
		// band's `current` is the only one that MOVES - which is both the decode and the reason the live value has to
		// be stamped at send time (WorldObjectFromColumnsTests pins the stamp itself).
		[Fact]
		public async Task HarvestNodes_AreTheThousandMaxBand_AndAreShippedPartlyHarvested()
		{
			var rows = await LoadDestructibles();

			var nodes = rows.Where(r => HarvestCatalog.IsHarvestable(r.DisplayCategory, r.DisplayId)).ToList();
			Assert.NotEmpty(nodes);
			Assert.All(nodes, n => Assert.Equal(HarvestCatalog.NodeMaxHealth, Trailer(n)[WorldObjectGeometryColumns.HealthMaxWord]));

			// The bug this decode exposed: most shipped nodes are NOT full. Asserted as a positive so the fix cannot be
			// "quietly re-import full nodes" - the captured rows stay as captured, and the send path is what corrects.
			var depleted = nodes.Count(n => Trailer(n)[WorldObjectGeometryColumns.HealthCurrentWord] < HarvestCatalog.NodeMaxHealth);
			Assert.True(depleted > nodes.Count / 2,
				$"only {depleted} of {nodes.Count} shipped nodes are mid-harvest - if that has changed, the live-health stamp may no longer be needed");
		}

		// The guild-city half: the plot markers are a single uniform, undamaged population, and the built battlekeep
		// sits two orders of magnitude above them. Both are far outside anything a harvest node reaches, which is what
		// lets one message type carry both.
		[Fact]
		public async Task GuildCityPlots_AreUniform_AndTheBuiltKeepIsTiered()
		{
			var rows = await LoadDestructibles();

			var plots = rows.Where(r => r.Name?.StartsWith("Location Object - ") == true).ToList();
			Assert.NotEmpty(plots);
			Assert.All(plots, p =>
			{
				var trailer = Trailer(p);
				Assert.Equal(1_000_000u, trailer[WorldObjectGeometryColumns.HealthMaxWord]);
				Assert.Equal(1_000_000u, trailer[WorldObjectGeometryColumns.HealthCurrentWord]); // never damaged: unbuilt
			});

			// `Keep III` is the top of the ladder, and the ladder is monotone in the tier suffix.
			var keep3 = rows.Where(r => r.Name == "Keep III").ToList();
			if (keep3.Count == 0)
				return; // the battlekeep zone is not in every DB slice

			var keepMax = Trailer(keep3[0])[WorldObjectGeometryColumns.HealthMaxWord];
			Assert.Equal(300_000_000u, keepMax);
			Assert.All(plots, p => Assert.True(Trailer(p)[WorldObjectGeometryColumns.HealthMaxWord] < keepMax));
		}

		private static uint[] Trailer(WorldObject row)
		{
			return [.. row.Words.Where(w => w.Kind == WorldObjectWordKind.Trailer).OrderBy(w => w.Ordinal).Select(w => w.Value)];
		}

		private static async Task<WorldObject[]> LoadDestructibles()
		{
			await using var ctx = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			return await ctx.WorldObjects.Include(o => o.Words).Where(o => o.SubOpcode == Destructible)
				.ToArrayAsync(TestContext.Current.CancellationToken);
		}
	}
}
