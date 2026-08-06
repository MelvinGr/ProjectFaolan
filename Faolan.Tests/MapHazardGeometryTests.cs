using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The evidence tying the client's hazard GEOMETRY to the server behaviour retail actually fired (#504) - the test
	// the `[V2:]` citation in docs/rdb/bounded-areas-1000053.md resolves to.
	//
	// `BA_Lava7` is the lava volume on Ymir's Pass (map 1090) that killed the player in
	// NecroL80_mage-necromancer_login_gameplay_chat-annotated-4. Its 54-vertex polygon and its 235.0..241.7 height range
	// are the client's own, straight out of RDB type 1000053; the positions below are the player's own CharDCMove track
	// through the moment the server sent its `GenericHazardiousBoundedArea` feedback at 20:25:58.706.
	//
	// The polygon is inlined rather than read from faolan.db on purpose: this gate is about the GEOMETRY agreeing with
	// the wire, so it must keep failing if a re-import changes the shape, and it must run from a plain clone with no
	// client RDB present.
	//
	// The volume is a `MapRegion` and not a `MapHazard` since #679: a hazard is the BEHAVIOUR half (what it applies,
	// whether it kills) hanging off the client's volume, so the geometry this gate is about lives on the region.
	public class MapHazardGeometryTests
	{
		// (x, z) pairs in RDB ring order. Implicitly closed - the first point is not repeated.
		private static readonly float[] Lava7Ring =
		[
			1286.0055f, 465.0130f, 1288.3949f, 463.4000f, 1288.5363f, 462.9823f,
			1288.4036f, 462.3230f, 1287.2654f, 461.3480f, 1288.9644f, 461.1092f,
			1290.0911f, 460.3345f, 1290.5820f, 459.5759f, 1290.4736f, 458.3344f,
			1290.3466f, 457.4308f, 1289.2979f, 456.5482f, 1290.7379f, 453.9343f,
			1291.4781f, 451.0078f, 1289.4049f, 449.6935f, 1291.0789f, 448.2198f,
			1292.1637f, 446.1960f, 1292.5817f, 444.4675f, 1293.4003f, 444.7403f,
			1293.8176f, 444.3136f, 1294.0576f, 443.7333f, 1294.9380f, 443.4794f,
			1295.1221f, 442.1725f, 1294.9163f, 440.4229f, 1295.8931f, 441.1394f,
			1299.1437f, 441.6952f, 1298.8087f, 443.9870f, 1297.9924f, 443.6473f,
			1293.4071f, 449.0849f, 1292.3982f, 452.1633f, 1292.2412f, 453.8277f,
			1291.4425f, 456.9000f, 1291.5984f, 459.2566f, 1290.7266f, 461.1819f,
			1291.6320f, 464.1482f, 1291.1802f, 467.7862f, 1292.1846f, 470.7294f,
			1291.2303f, 475.7280f, 1293.1294f, 479.0214f, 1293.3771f, 480.6640f,
			1290.9308f, 480.5930f, 1290.0886f, 478.5287f, 1290.5515f, 475.8210f,
			1289.0045f, 475.4702f, 1286.5658f, 466.5895f, 1284.7932f, 465.9381f,
			1281.7317f, 466.2353f, 1280.6196f, 467.0062f, 1278.2112f, 466.4522f,
			1276.8580f, 464.3896f, 1276.0709f, 463.4750f, 1272.3391f, 462.3679f,
			1273.5176f, 461.5400f, 1275.2941f, 461.7766f, 1281.7207f, 464.0984f
		];

		private static MapRegion Lava7()
		{
			var region = new MapRegion
			{
				MapId = 1090,
				Name = "BA_Lava7",
				MinY = 235.0f,
				MaxY = 241.6999969482422f,
				MinX = 1272.339111328125f,
				MaxX = 1299.1436767578125f,
				MinZ = 440.4229431152344f,
				MaxZ = 480.6639709472656f,
				Hazard = new MapHazard { EffectSpellId = 3773837, Lethal = true }
			};

			for (var i = 0; i < Lava7Ring.Length / 2; i++)
			{
				region.Vertices.Add(new MapRegionVertex
				{
					Sequence = i,
					X = Lava7Ring[i * 2],
					Z = Lava7Ring[i * 2 + 1]
				});
			}

			return region;
		}

		// The crossing, sample by sample. This is the whole claim: the client's polygon and the server's trigger are
		// the same boundary, to within one ~200 ms position sample.
		//
		//   20:25:57.783  outside
		//   20:25:58.281  outside (0.30 units short of the edge)
		//   20:25:58.444  INSIDE   <- first sample in
		//   20:25:58.706           <- the GenericHazardiousBoundedArea feedback, 262 ms later
		//   20:25:58.745+ INSIDE
		[Theory]
		[InlineData(1286.6f, 241.2f, 489.5f, false)] // 20:25:56.309, still well outside
		[InlineData(1286.7f, 240.4f, 478.0f, false)] // 20:25:57.783
		[InlineData(1288.4f, 239.2f, 474.4f, false)] // 20:25:58.281 - the last sample before the crossing
		[InlineData(1289.3f, 238.3f, 473.5f, true)] // 20:25:58.444 - the first sample inside; feedback follows in 262 ms
		[InlineData(1290.8f, 238.1f, 472.2f, true)] // 20:25:58.745
		[InlineData(1289.0f, 238.5f, 473.8f, true)] // 20:25:59.986 - still inside while burning
		public void CapturedTrack_CrossesTheClientPolygonWhereRetailFired(float x, float y, float z, bool expected)
		{
			Assert.Equal(expected, EnvironmentService.Contains(Lava7(), new Vector3(x, y, z)));
		}

		// The prism's HEIGHT range is load-bearing, not decoration: Ymir's Pass lava sits at 232..269 across its 22
		// volumes and there is walkable ground above some of it, so an (x, z)-only test would kill players overhead.
		[Theory]
		[InlineData(300f)] // far above the flow
		[InlineData(241.8f)] // just over MaxY
		[InlineData(234.9f)] // just under MinY
		public void APositionOverOrUnderTheFlow_IsNotInIt(float y)
		{
			// (1289.3, z 473.5) is inside the polygon in plan view - only the height keeps it out.
			Assert.False(EnvironmentService.Contains(Lava7(), new Vector3(1289.3f, y, 473.5f)));
		}

		// The AABB is a cheap reject, never the containment test. These points are all inside BA_Lava7's bounding box
		// and its height range, yet outside the actual flow - which is exactly why the polygon walk has to happen. A
		// volume that kills on an AABB hit would kill players standing beside the lava.
		[Theory]
		[InlineData(1275.0f, 480.0f)]
		[InlineData(1298.0f, 470.0f)]
		[InlineData(1285.0f, 445.0f)]
		public void InsideTheBoundingBox_IsNotInsideTheFlow(float x, float z)
		{
			var region = Lava7();
			Assert.InRange(x, region.MinX, region.MaxX);
			Assert.InRange(z, region.MinZ, region.MaxZ);
			Assert.False(EnvironmentService.Contains(region, new Vector3(x, 238.0f, z)));
		}

		// The ring is implicitly closed. Dropping the wrap-around edge leaks the polygon open along it, so this asserts
		// the closing edge is walked: a point that is only excluded BY that edge must read as outside.
		[Fact]
		public void TheRingIsClosed_AndAHazardWithoutEnoughVerticesIsInert()
		{
			var degenerate = new MapRegion
			{
				MapId = 1090,
				Name = "degenerate",
				MinY = 0f,
				MaxY = 100f,
				MinX = 0f,
				MaxX = 10f,
				MinZ = 0f,
				MaxZ = 10f
			};
			degenerate.Vertices.Add(new MapRegionVertex { Sequence = 0, X = 0f, Z = 0f });
			degenerate.Vertices.Add(new MapRegionVertex { Sequence = 1, X = 10f, Z = 10f });

			Assert.False(EnvironmentService.Contains(degenerate, new Vector3(5f, 50f, 5f)));
		}

		// Containment must not depend on the order EF happened to materialise the vertices in - a polygon read out of
		// ring order is a different (self-intersecting) shape. Shuffling the collection while keeping Sequence intact
		// must change nothing.
		[Fact]
		public void VertexOrder_ComesFromSequence_NotFromTheCollectionOrder()
		{
			var shuffled = Lava7();
			var reversed = new List<MapRegionVertex>(shuffled.Vertices);
			reversed.Reverse();
			shuffled.Vertices.Clear();
			foreach (var v in reversed)
				shuffled.Vertices.Add(v);

			Assert.True(EnvironmentService.Contains(shuffled, new Vector3(1289.3f, 238.3f, 473.5f)));
			Assert.False(EnvironmentService.Contains(shuffled, new Vector3(1288.4f, 239.2f, 474.4f)));
		}
	}
}
