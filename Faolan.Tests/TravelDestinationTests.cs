using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #514/#635: local travel - the dialogue branch that sells a trip, its fare and its arrival.
	//
	// The rows come from TWO modules and only one of them runs by default:
	//   * `travel-wiring`         (Captures)          - the wagoner trips, harvested from captured dialogue
	//   * `rdb-dialogue-teleports` (Rdb, ⚠️ OPT-IN)   - trips sold by an inline {teleportplayer} directive
	//
	// ⚠️ And `TravelDestinations.FromNodeId` is an FK to `ConversationNodes`, which the `conversations` module
	// rebuilds WHOLE-CORPUS. So a default import cascade-deletes every travel row, `travel-wiring` re-adds its own,
	// and the opt-in module's are simply gone - silently, because nothing counts them. That is exactly what
	// happened on 2026-08-08: an ordinary `--skip characters` run took the table 7 -> 4 and the three lost trips
	// (Tortage Smuggler, Wagoner Polus, Sigurd) were all `FromDialogueDirective`.
	//
	// This gate is what makes that loud. If it fires after an import, re-run:
	//     dotnet run --project Faolan.Importer -- --only rdb-dialogue-teleports
	public class TravelDestinationTests
	{
		[Fact]
		public async Task DirectiveSourcedTrips_SurviveAConversationRebuild()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var trips = await db.TravelDestinations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			var directive = trips.Count(t => t.FromDialogueDirective);
			var captured = trips.Count(t => !t.FromDialogueDirective);

			Assert.True(directive >= 3,
				$"only {directive} directive-sourced travel destination(s) are shipped (expected at least 3: Tortage Smuggler, "
				+ "Wagoner Polus, Sigurd). The `conversations` module rebuilds ConversationNodes whole-corpus and the FK "
				+ "cascade takes these with it, so a default import drops them. Re-run the OPT-IN module that owns them:\n"
				+ "    dotnet run --project Faolan.Importer -- --only rdb-dialogue-teleports");

			Assert.True(captured >= 4, $"only {captured} capture-harvested travel destination(s) are shipped (expected at least 4 wagoner trips).");
		}

		// Every trip must land somewhere we actually hold, or the player is teleported into a map that does not exist.
		[Fact]
		public async Task EveryTrip_ArrivesOnAnImportedMap()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var maps = (await db.Maps.Select(m => m.Id).ToListAsync(ct)).ToHashSet();

			var orphans = (await db.TravelDestinations.AsNoTracking().ToListAsync(ct))
				.Where(t => !maps.Contains(t.DestinationMapId))
				.Select(t => $"{t.NpcName} -> map {t.DestinationMapId}")
				.ToList();

			Assert.True(orphans.Count == 0, "travel destinations arriving on a map we do not hold:\n  " + string.Join("\n  ", orphans));
		}
	}
}
