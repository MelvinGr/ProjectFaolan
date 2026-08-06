using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The tiered NpcSpawnArea creature resolver (#363): tier 1 name, tier 2 gated containment, tier 3 guarded
	// id-propagation, tier 4 the group's own declared race (#518). The headline case is the shared-community guard -
	// Slaver1 and Poacher1 both sit under Community 60, so id-propagation would wrongly write "Poacher" onto the
	// slavers; the name-disagreement guard must block it. Tier 4's gates are the same idea asked of the RACE.
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

		// #489: a numeric id names a FAMILY, not a species, so an observation on one playfield says nothing about the
		// same family on another. Before this gate 319 of 329 propagations (3697 NPCs across 17 maps) rested on
		// evidence from a different map - Poitain's 29 groups all became "Panther" off a (Superfamily, 4) key whose
		// only captured panthers are in Tortage, while that map's own captures say Wild Pig / Wild Cow.
		[Fact]
		public async Task IdPropagation_Requires_Same_Map_Evidence()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-x-{Guid.NewGuid():N}.db");
			try
			{
				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					await Seed(db);
					db.Maps.Add(new Map
					{
						Id = 4020,
						Name = "City of Tortage"
					});
					// Same Race 31 key, but on a map with no local name/containment resolution of it.
					db.NpcSpawnAreas.Add(Area(4020, 0, "Ape7", "Race", 31, 500, 500, 510, 510));
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					await SpawnAreaResolver.ResolveAsync(db);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					var onEvidenceMap = await db.NpcSpawnAreas.SingleAsync(a => a.MapId == 4010 && a.GroupOrdinal == 3, TestContext.Current.CancellationToken);
					Assert.Equal(101u, onEvidenceMap.NpcTemplateId); // Ape5, same map as the Ape2 that defined Race 31
					Assert.Equal(SpawnResolutionSource.IdPropagation, onEvidenceMap.ResolvedBy);

					var offEvidenceMap = await db.NpcSpawnAreas.SingleAsync(a => a.MapId == 4020, TestContext.Current.CancellationToken);
					Assert.Null(offEvidenceMap.NpcTemplateId); // no Race 31 evidence on 4020 - honestly unknown
					Assert.Equal(SpawnResolutionSource.None, offEvidenceMap.ResolvedBy);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		// The resolver must be IDEMPOTENT: it resets machine assignments before re-deriving, so a rule change can
		// actually take effect. Tier 3 only ever fills areas still at None, so without the reset an area already
		// carrying a now-disallowed guess would be skipped and keep it forever.
		[Fact]
		public async Task Stale_Machine_Assignment_Is_Cleared_When_No_Longer_Supported()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-r-{Guid.NewGuid():N}.db");
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
					db.NpcTemplates.Add(Spawnable(101, "Jungle Ape"));
					// A guess left over from an earlier run, with nothing in this DB supporting it.
					var stale = Area(4010, 0, "Territory1", "Superfamily", 4);
					stale.NpcTemplateId = 101;
					stale.ResolvedBy = SpawnResolutionSource.IdPropagation;
					db.NpcSpawnAreas.Add(stale);
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					await SpawnAreaResolver.ResolveAsync(db);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					var a = await db.NpcSpawnAreas.SingleAsync(TestContext.Current.CancellationToken);
					Assert.Null(a.NpcTemplateId);
					Assert.Equal(SpawnResolutionSource.None, a.ResolvedBy);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		// #491: the roam AABB also contains hand-placed NPCs standing inside it, and their votes break the containment
		// tier's unanimity. A group's real members roam ALONG its RDB path polyline, so the vote is taken on the
		// ON-PATH spawns; the whole box is only a fallback, and then only when the group's name does not contradict
		// the creature.
		[Fact]
		public async Task Containment_Votes_On_The_Roam_Path_Not_The_Whole_Box()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-p-{Guid.NewGuid():N}.db");
			try
			{
				var areaId = NpcSpawnArea.ComputeId(4010, 0);
				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					db.Maps.Add(new Map
					{
						Id = 4010,
						Name = "Tortage Island"
					});
					db.NpcTemplates.AddRange(Spawnable(101, "Jungle Ape"), Spawnable(102, "Hand Picked"));
					// Two apes ON the path at z=0, and two vendors standing off it inside the same box. Under a plain
					// AABB vote the box is split 2-2 and resolves to nothing.
					db.NpcSpawns.AddRange(
						Spawn(4010, 101, 2, 0), Spawn(4010, 101, 8, 0),
						Spawn(4010, 102, 2, 40), Spawn(4010, 102, 8, 40));
					db.NpcSpawnAreas.Add(Area(4010, 0, "Ape9", "Race", 31, 0, 0, 50, 50));
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					// One path segment running along z=0, so only the apes are standing on it.
					var paths = new Dictionary<uint, IReadOnlyList<IReadOnlyList<Vector3>>>
					{
						[areaId] = new[]
						{
							new[]
							{
								new Vector3(0, 0, 0),
								new Vector3(50, 0, 0)
							}
						}
					};
					await SpawnAreaResolver.ResolveAsync(db, paths);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					var a = await db.NpcSpawnAreas.SingleAsync(TestContext.Current.CancellationToken);
					Assert.Equal(101u, a.NpcTemplateId); // the apes on the path win; the vendors beside it do not vote
					Assert.Equal(SpawnResolutionSource.Containment, a.ResolvedBy);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		private static NpcSpawn Spawn(uint map, uint template, float x, float z)
		{
			return new NpcSpawn
			{
				Id = NpcSpawn.ComputeId(map, x, 0, z, null),
				MapId = map,
				NpcTemplateId = template,
				PositionX = x,
				PositionY = 0,
				PositionZ = z
			};
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
				Id = NpcSpawn.ComputeId(4010, 5, 0, 5, null),
				MapId = 4010,
				NpcTemplateId = 101,
				PositionX = 5,
				PositionY = 0,
				PositionZ = 5
			}, new NpcSpawn
			{
				Id = NpcSpawn.ComputeId(4010, 6, 0, 6, null),
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

		private static NpcTemplate Spawnable(uint id, string name, uint? race = null)
		{
			return new NpcTemplate
			{
				TemplateId = id,
				Name = name,
				StatCore = new NpcStatCore
				{
					NpcTemplateId = id,
					Race = race
				}
			};
		}

		// --- Tier 4: the group's own DECLARED race (#518) -----------------------------------------------------
		// A `Race`-category group's id IS the RDB 1000508 race id, so where we hold exactly ONE template of that race
		// there is nothing to choose between and the area resolves. The three gates are what keep it off a guess:
		// the race must be unambiguous, the group's own name must not contradict it, and it must not displace a
		// higher tier.

		private static CreatureRaces Races(params (uint Id, string Name)[] rows)
		{
			// Build the record the reader expects rather than the dictionary, so the gates exercise the real parse:
			// [type][id][version][count] then count x [raceId][nameLength][name].
			using var ms = new MemoryStream();
			using var w = new BinaryWriter(ms);
			w.Write(CreatureRaceTable.RdbType);
			w.Write(0u);
			w.Write(1u);
			w.Write((uint)rows.Length);
			foreach (var (id, name) in rows)
			{
				w.Write(id);
				w.Write((uint)name.Length);
				w.Write(Encoding.Latin1.GetBytes(name));
			}

			w.Flush();
			return CreatureRaceTable.Parse(ms.ToArray());
		}

		[Fact]
		public void TheRaceTable_ParsesToItsOwnDeclaredCount()
		{
			var races = Races((72, "Rabbit"), (56, "Rat"), (77, "Giant"));

			Assert.True(races.Exact);
			Assert.Equal(0, races.TrailingBytes);
			Assert.Equal("Rabbit", races.Races[72]);
		}

		// The compound case that made the first cut of this tier under-resolve by 19 areas: `GiantRat01` names Giant
		// AND Rat, and taking only the FIRST would make the group contradict its own declared race.
		[Fact]
		public void AGroupNameNamingSeveralRaces_CorroboratesWhenAnyOfThemIsTheDeclaredOne()
		{
			var races = Races((72, "Rabbit"), (56, "Rat"), (77, "Giant"));

			Assert.Equal([56u, 77u], races.MatchRaces("GiantRat01").Order());
			Assert.False(races.ContradictsRace("GiantRat01", 56));  // names Rat -> corroborates
			Assert.False(races.ContradictsRace("Territory14", 56)); // names no race -> says nothing, so blocks nothing
			Assert.True(races.ContradictsRace("RabbitWarren2", 56)); // names ONLY Rabbit -> a real conflict
		}

		[Fact]
		public async Task AnUnambiguousRace_ResolvesTheArea_EvenWhenTheTemplateNameSharesNoWordWithTheGroup()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-r-{Guid.NewGuid():N}.db");
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

					// "Old Buck" is the shipped rabbit, and shares no word with "GrasslandsRabbit42" - the whole point
					// of guarding on the RACE name rather than the creature's.
					db.NpcTemplates.Add(Spawnable(100, "Old Buck", 72));
					db.NpcSpawnAreas.Add(Area(4010, 0, "GrasslandsRabbit42", "Race", 72));
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					var stats = await SpawnAreaResolver.ResolveAsync(db, null, Races((72, "Rabbit")));
					Assert.Equal(1, stats.RaceUnique);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					var a = await db.NpcSpawnAreas.SingleAsync(TestContext.Current.CancellationToken);
					Assert.Equal(100u, a.NpcTemplateId);
					Assert.Equal(SpawnResolutionSource.RaceUnique, a.ResolvedBy);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		// The gate that keeps this from being the guess #518 argues against: a race we hold SEVERAL templates for
		// says which family lives here but not which creature, so the area stays honestly unresolved.
		[Fact]
		public async Task AnAmbiguousRace_LeavesTheAreaUnresolved_RatherThanPickingOne()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-ra-{Guid.NewGuid():N}.db");
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
					db.NpcTemplates.AddRange(Spawnable(100, "Rotting Corpse", 93), Spawnable(101, "Risen Dead", 93));
					db.NpcSpawnAreas.Add(Area(4010, 0, "CaveUndead1", "Race", 93));
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					var stats = await SpawnAreaResolver.ResolveAsync(db, null, Races((93, "Undead")));
					Assert.Equal(0, stats.RaceUnique);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
				{
					var a = await db.NpcSpawnAreas.SingleAsync(TestContext.Current.CancellationToken);
					Assert.Null(a.NpcTemplateId);
					Assert.Equal(SpawnResolutionSource.None, a.ResolvedBy);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		// A group whose own name names a DIFFERENT race than it declares is left alone - the same shape as tier 3's
		// Slaver-under-Poacher guard, asked of the race instead of the creature.
		[Fact]
		public async Task AGroupNameContradictingItsDeclaredRace_IsLeftUnresolved()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-resolver-rc-{Guid.NewGuid():N}.db");
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
					db.NpcTemplates.Add(Spawnable(100, "Mouse", 56));
					db.NpcSpawnAreas.Add(Area(4010, 0, "CatTerritory", "Race", 56)); // declares Rat, names Cat
					await db.SaveChangesAsync(TestContext.Current.CancellationToken);

					var stats = await SpawnAreaResolver.ResolveAsync(db, null, Races((56, "Rat"), (43, "Cat")));
					Assert.Equal(0, stats.RaceUnique);
				}

				await using (var db = new DatabaseContext(TestDb.Options(path)))
					Assert.Null((await db.NpcSpawnAreas.SingleAsync(TestContext.Current.CancellationToken)).NpcTemplateId);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}
	}
}
