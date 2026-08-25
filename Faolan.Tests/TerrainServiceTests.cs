using System;
using System.IO;
using System.Numerics;
using Faolan.Core.Rdb.Assets;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The aggro line-of-sight gate (#284 tier 2/3) as WorldState consults it, on a synthetic bake: a flat 64x64 grid at
	// Y=100 with, per test, a terrain hump or a collision actor between an NPC and a player 20 u apart. Locks the two
	// #577 changes - the collision half's three-ray test (a post is not cover) and the terrain half standing aside for
	// an endpoint the heightmap does not describe (a cellar NPC is not melee-only) - and the Terrain-before-Collision
	// attribution `--report terrain-los` relies on.
	public sealed class TerrainServiceTests : IDisposable
	{
		private const uint MapId = 7;
		private const int Extent = 64;
		private const float Ground = 100f;
		private static readonly Vector3 Npc = new(10, Ground, 32);
		private static readonly Vector3 Player = new(30, Ground, 32);

		private readonly string _dir = Path.Combine(Path.GetTempPath(), $"faolan-terrain-{Guid.NewGuid():N}");

		public TerrainServiceTests()
		{
			Directory.CreateDirectory(_dir);
		}

		public void Dispose()
		{
			Directory.Delete(_dir, true);
		}

		[Fact]
		public void Flat_Open_Ground_IsClear()
		{
			var terrain = Bake(Flat(), []);
			Assert.Equal(LineOfSightBlocker.None, terrain.Trace(MapId, Npc, Player));
			Assert.True(terrain.HasLineOfSight(MapId, Npc, Player));
		}

		[Fact]
		public void A_Hump_Above_Both_Eyes_Blocks_ByTerrain()
		{
			// A ridge 4 u high across the middle: above the 1.5 u eyes by more than the 1.5 u margin.
			var terrain = Bake(Flat((gx, _) => gx is >= 19 and <= 21
				? Ground + 4
				: Ground), []);
			Assert.Equal(LineOfSightBlocker.Terrain, terrain.Trace(MapId, Npc, Player));
		}

		[Fact]
		public void A_Bump_Inside_The_Margin_Does_Not_Block()
		{
			// 2.5 u: above the eyes, but not by the margin - the slack that keeps a dune from reading as a wall.
			var terrain = Bake(Flat((gx, _) => gx is >= 19 and <= 21
				? Ground + 2.5f
				: Ground), []);
			Assert.Equal(LineOfSightBlocker.None, terrain.Trace(MapId, Npc, Player));
		}

		[Fact]
		public void A_Post_Between_Them_Is_Seen_Past()
		{
			// A 0.4 u wide post square on the eye-line. The centre ray hits it - that is what made a single-ray gate
			// call it cover - and both shoulder rays, half a unit to either side, miss it.
			var post = Wall(20, 31.8f, 32.2f);
			var terrain = Bake(Flat(), post);

			Assert.True(RdbCollisionMesh.TryParse(post, out var mesh));
			Assert.True(mesh.SegmentBlocked(Eye(Npc), Eye(Player))); // the single ray IS blocked ...
			Assert.Equal(LineOfSightBlocker.None, terrain.Trace(MapId, Npc, Player)); // ... and the gate sees past it
		}

		[Fact]
		public void A_Wall_Wider_Than_The_Shoulders_Blocks_ByCollision()
		{
			// 8 u wide: all three rays end in it.
			var terrain = Bake(Flat(), Wall(20, 28, 36));
			Assert.Equal(LineOfSightBlocker.Collision, terrain.Trace(MapId, Npc, Player));
			Assert.False(terrain.HasLineOfSight(MapId, Npc, Player));
		}

		[Fact]
		public void A_Wall_Covering_Only_One_Shoulder_Does_Not_Block()
		{
			// Ends exactly on the far shoulder ray's line: the centre and one shoulder are blocked, the other clear.
			var terrain = Bake(Flat(), Wall(20, 28, 32.4f));
			Assert.Equal(LineOfSightBlocker.None, terrain.Trace(MapId, Npc, Player));
		}

		[Fact]
		public void Terrain_Is_Attributed_Before_Collision()
		{
			var terrain = Bake(Flat((gx, _) => gx is >= 19 and <= 21
				? Ground + 4
				: Ground), Wall(20, 28, 36));
			Assert.Equal(LineOfSightBlocker.Terrain, terrain.Trace(MapId, Npc, Player));
		}

		[Fact]
		public void An_Npc_Under_The_Surface_Is_Not_Blocked_By_The_Roof_Over_It()
		{
			// A cellar: the NPC and the player both stand 10 u under the grid. Every sample along the ray reads the
			// surface 10 u overhead, so a gate that trusted the heightmap here occluded at every range - the melee-only
			// aggro of #577 for the 7 City of Tortage spawns that sit under their grid.
			var terrain = Bake(Flat(), []);
			var npc = Npc with { Y = Ground - 10 };
			var player = Player with { Y = Ground - 10 };
			Assert.Equal(LineOfSightBlocker.None, terrain.Trace(MapId, npc, player));
		}

		[Fact]
		public void Under_The_Surface_The_Collision_Mesh_Still_Gates()
		{
			// The terrain half standing aside does not open the gate: the cellar's own wall (collision) still blocks.
			var terrain = Bake(Flat(), Wall(20, 28, 36, Ground - 15, Ground + 5));
			var npc = Npc with { Y = Ground - 10 };
			var player = Player with { Y = Ground - 10 };
			Assert.Equal(LineOfSightBlocker.Collision, terrain.Trace(MapId, npc, player));
		}

		[Fact]
		public void Either_Endpoint_Under_The_Surface_Counts()
		{
			// Only the PLAYER is under the grid (a cellar the NPC on the street cannot see into is the collision mesh's
			// job, not the heightmap's): the terrain half stands aside for that end too.
			var terrain = Bake(Flat(), []);
			var player = Player with { Y = Ground - 10 };
			Assert.Equal(LineOfSightBlocker.None, terrain.Trace(MapId, Npc, player));
		}

		[Fact]
		public void Without_A_Bake_Every_Ray_Is_Clear()
		{
			var terrain = new TerrainService(_dir, NullLogger.Instance); // dir exists, no file for MapId
			Assert.True(terrain.Available);
			Assert.Equal(LineOfSightBlocker.None, terrain.Trace(MapId, Npc, Player));
		}

		private TerrainService Bake(byte[] heightmap, byte[] collision)
		{
			using (var fs = File.Create(Path.Combine(_dir, $"{MapId}{TerrainFile.Extension}")))
				TerrainFile.Write(fs, Extent, heightmap, collision);

			var terrain = new TerrainService(_dir, NullLogger.Instance);
			Assert.True(terrain.Available);
			Assert.Equal(Ground, terrain.GroundHeight(MapId, 5, 5) ?? float.NaN);
			return terrain;
		}

		private static Vector3 Eye(Vector3 feet)
		{
			return feet with { Y = feet.Y + TerrainService.EyeHeight };
		}

		private static byte[] Flat(Func<int, int, float>? height = null)
		{
			return RdbHeightmapTests.Build(Extent, Extent, height ?? ((_, _) => Ground));
		}

		// A vertical quad in the X=x plane spanning Z[z0,z1] and Y[y0,y1] (default: from below the ground to well above
		// the eyes), as one two-triangle collision actor.
		private static byte[] Wall(float x, float z0, float z1, float y0 = Ground - 5, float y1 = Ground + 10)
		{
			Vector3[] verts = [new(x, y0, z0), new(x, y1, z0), new(x, y0, z1), new(x, y1, z1)];
			return RdbCollisionMeshTests.Actor(verts, [(0, 1, 2), (1, 3, 2)]);
		}
	}
}
