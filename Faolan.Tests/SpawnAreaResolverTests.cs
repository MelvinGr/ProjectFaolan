using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The tiered NpcSpawnArea creature resolver (#363): tier 1 name, tier 2 gated containment, tier 3 guarded
	// id-propagation. The headline case is the shared-community guard - Slaver1 and Poacher1 both sit under Community
	// 60, so id-propagation would wrongly write "Poacher" onto the slavers; the name-disagreement guard must block it.
	public class SpawnAreaResolverTests
	{
		[Fact]
		public async Task Manual_Assignment_Is_Never_Overwritten()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-m-{Guid.NewGuid():N}.db");
			try
			{
				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					db.Maps.Add(new Map
					{
						Id = 4010,
						Name = "Tortage Island"
					});
					db.NpcTemplates.AddRange(Spawnable(100, "Poacher"), Spawnable(200, "Hand Picked"));
					var a = Area(4010, 0, "Poacher1", "Community", 60);
					a.NpcTemplateId = 200; // a human said this area is "Hand Picked"
					a.ResolvedBy = SpawnResolutionSource.Manual;
					db.NpcSpawnAreas.Add(a);
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					await SpawnAreaResolver.ResolveAsync(db);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					var a = await db.NpcSpawnAreas.SingleAsync(TestContext.Current.CancellationToken);
					Assert.Equal(200u, a.NpcTemplateId); // NOT overwritten by the name tier's Poacher (100)
					Assert.Equal(SpawnResolutionSource.Manual, a.ResolvedBy);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task Resolves_Tiers_And_Blocks_Shared_Id_Mislabel()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-{Guid.NewGuid():N}.db");
			try
			{
				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					await Seed(db);
					var stats = await SpawnAreaResolver.ResolveAsync(db);
					Assert.Equal(1, stats.Name);
					Assert.Equal(1, stats.Containment);
					Assert.Equal(1, stats.IdProp);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					var byOrd = await db.NpcSpawnAreas.ToDictionaryAsync(a => a.GroupOrdinal, TestContext.Current.CancellationToken);

					Assert.Equal(100u, byOrd[0].NpcTemplateId); // Poacher1 -> Poacher
					Assert.Equal(SpawnResolutionSource.Name, byOrd[0].ResolvedBy);

					Assert.Equal(101u, byOrd[2].NpcTemplateId); // Ape2 -> Jungle Ape (containment)
					Assert.Equal(SpawnResolutionSource.Containment, byOrd[2].ResolvedBy);

					Assert.Equal(101u, byOrd[3].NpcTemplateId); // Ape5 -> Jungle Ape (id-prop, name agrees)
					Assert.Equal(SpawnResolutionSource.IdPropagation, byOrd[3].ResolvedBy);

					// The guard: Slaver1 is NOT turned into a Poacher despite sharing Community 60.
					Assert.Null(byOrd[1].NpcTemplateId);
					Assert.Equal(SpawnResolutionSource.None, byOrd[1].ResolvedBy);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		private static NpcSpawnArea Area(uint map, int ord, string groupName, string cat, uint? communityId, float minX = 0, float minZ = 0, float maxX = 0, float maxZ = 0)
		{
			return new NpcSpawnArea
			{
				Id = NpcSpawnArea.ComputeId(map, ord),
				MapId = map,
				GroupOrdinal = ord,
				GroupName = groupName,
				Category = cat,
				GroupKey = communityId?.ToString() ?? groupName,
				CommunityId = communityId,
				BoundsMinX = minX,
				BoundsMinZ = minZ,
				BoundsMaxX = maxX,
				BoundsMaxZ = maxZ,
				MaxCount = 1,
				ResolvedBy = SpawnResolutionSource.None
			};
		}

		private static async Task Seed(DatabaseContext db)
		{
			db.Maps.Add(new Map
			{
				Id = 4010,
				Name = "Tortage Island"
			});
			db.NpcTemplates.AddRange(Spawnable(100, "Poacher"), Spawnable(101, "Jungle Ape"), Spawnable(102, "Crocodile"));
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);

			// Two captured Jungle Apes inside the Ape2 area's bounds -> unanimous containment resolution.
			// These keys are DERIVED from the placement (NpcSpawn.ComputeId), so they must be set here exactly as the
			// importer sets them - the table has no store-generated key, and two rows left at 0 collide on Add.
			db.NpcSpawns.AddRange(new NpcSpawn
			{
				Id = NpcSpawn.ComputeId(4010, 5, 0, 5),
				MapId = 4010,
				NpcTemplateId = 101,
				PositionX = 5,
				PositionY = 0,
				PositionZ = 5
			}, new NpcSpawn
			{
				Id = NpcSpawn.ComputeId(4010, 6, 0, 6),
				MapId = 4010,
				NpcTemplateId = 101,
				PositionX = 6,
				PositionY = 0,
				PositionZ = 6
			});

			// Bounds are placed so ONLY Ape2's region covers the captured apes at (5,5)/(6,6); the others sit far away so
			// they fall through to the tier they're meant to test.
			db.NpcSpawnAreas.AddRange(Area(4010, 0, "Poacher1", "Community", 60, 100, 100, 100, 100), // tier1 name -> Poacher; defines Community 60
				Area(4010, 1, "Slaver1", "Community", 60, 300, 300, 300, 300), // shared id 60, no slaver template -> GUARD must block
				Area(4010, 2, "Ape2", "Race", 31, 0, 0, 10, 10), // tier2 containment -> Jungle Ape; defines Race 31
				Area(4010, 3, "Ape5", "Race", 31, 500, 500, 510, 510)); // tier3 id-prop -> Jungle Ape (name agrees)
			await db.SaveChangesAsync(TestContext.Current.CancellationToken);
		}

		private static NpcTemplate Spawnable(uint id, string name)
		{
			return new NpcTemplate
			{
				TemplateId = id,
				Name = name,
				StatCore = new NpcStatCore
				{
					NpcTemplateId = id
				}
			};
		}
	}
}
