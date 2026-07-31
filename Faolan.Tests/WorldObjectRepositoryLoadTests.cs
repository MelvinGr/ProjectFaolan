using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The world objects the RUNTIME loads must build the same record as the ones a test hand-assembles.
	//
	// Written after doors, climbing vines and destructibles were invisible in-client for want of one `Include`.
	// WorldObjectGeometryCodec writes the geometry-node words and the per-class trailer out of `row.Words`, the repo
	// deliberately does not lazy-load, and `GetWorldObjects` did not include that navigation - so every object of a
	// class that owns words went out short (4 bytes for Door and ClimbingDynel, 20 for Destructible) and the client
	// dropped it: "ReadSubClass for IIR 866480048 (DoorFullUpdateIIR_c) failed ... datasize left in stream=0".
	//
	// WorldObjectFromColumnsTests did not catch it because it builds its row FROM a captured body, so the words are always
	// populated in memory; the create-editor burst golden did not either, because every editor scene prop is a
	// SimpleDynel, the one class that owns no words. Neither exercised the repository. This does.
	public class WorldObjectRepositoryLoadTests
	{
		[Fact]
		public async Task RepositoryLoadedObjects_BuildTheSameRecordAsFullyLoadedOnes()
		{
			var options = TestDb.ReadOnly(TestDb.GameDatabase);
			var repo = new DatabaseRepository(new TestContextFactory(options));
			var ct = TestContext.Current.CancellationToken;

			await using var ctx = new DatabaseContext(options);
			var maps = await ctx.WorldObjects.Where(o => o.DisplayKind != null).Select(o => o.MapId).Distinct().ToListAsync(ct);
			Assert.NotEmpty(maps);

			var checkedObjects = 0;
			var withWords = 0;
			foreach (var mapId in maps)
			{
				var viaRepo = await repo.GetWorldObjects(mapId);

				// The same rows with the navigation explicitly loaded - the reference the runtime has to match.
				var expected = await ctx.WorldObjects.Include(o => o.Words).Where(o => o.MapId == mapId && o.DisplayKind != null).AsNoTracking().ToDictionaryAsync(o => o.Id, ct);

				foreach (var row in viaRepo)
				{
					var reference = expected[row.Id];
					if (reference.Words.Count > 0)
						withWords++;

					Assert.Equal(reference.Words.Count, row.Words.Count);
					Assert.Equal(WorldObjectGeometryColumns.ToWire(reference), WorldObjectGeometryColumns.ToWire(row));
					checkedObjects++;
				}
			}

			// Guard the guard: if the seed ever stopped carrying word rows, every comparison above would pass trivially.
			Assert.True(withWords > 300, $"only {withWords} of {checkedObjects} shipped objects carry word rows - the seed looks wrong, so this test proves nothing");
		}

		private sealed class TestContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(System.Threading.CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
