using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// The RDB collision-mesh (type 1000012, NovodeX PhysX cooked trimesh) parser + segment raycast for #284 tier-3
	// (building-wall line-of-sight). Format decoded + validated against the live client (beach 4010 = 666 actors /
	// 116,178 tris, 0 bad-parse; verts are world-space). These tests build a synthetic NXUSTREAM (one wall quad) and
	// lock the parse + the segment-vs-triangle raycast.
	public class RdbCollisionMeshTests
	{
		[Fact]
		public void Parses_World_Triangles()
		{
			Assert.True(RdbCollisionMesh.TryParse(Wall(), out var mesh));
			Assert.Equal(2, mesh.TriangleCount);
		}

		[Fact]
		public void RealBeachCollision_MatchesPythonValidation()
		{
			// Non-hermetic: only runs where the client RDB is present (set AOC_RDB to the client's RDB folder).
			// Locks the C# parser to the Python decode (beach 4010 = 666 actors / 116,178 triangles) so a
			// real-data regression is caught, not just the synthetic.
			var rdbDir = Environment.GetEnvironmentVariable("AOC_RDB");
			if (string.IsNullOrEmpty(rdbDir) || !File.Exists(Path.Combine(rdbDir, "le.idx")))
				return; // client RDB unavailable (CI) - skip

			using var rdb = new RdbReader(rdbDir);
			Assert.True(rdb.TryGet(RdbCollisionMesh.ResourceType, 4010, out var e));
			var body = rdb.ReadDecompressed(e);
			Assert.True(RdbCollisionMesh.TryParse(body, out var mesh));
			Assert.Equal(116178, mesh.TriangleCount);
		}

		[Fact]
		public void Segment_Beside_The_Wall_Is_Clear()
		{
			RdbCollisionMesh.TryParse(Wall(), out var mesh);
			Assert.False(mesh!.SegmentBlocked(new Vector3(0, 5, 50), new Vector3(20, 5, 50))); // Z=50 misses the quad
			Assert.False(mesh.SegmentBlocked(new Vector3(0, 50, 5), new Vector3(20, 50, 5))); // Y=50 above the quad
		}

		[Fact]
		public void Segment_That_Stops_Before_The_Wall_Is_Clear()
		{
			RdbCollisionMesh.TryParse(Wall(), out var mesh);
			// Both endpoints on the near side of X=10 - the wall is beyond the segment, so it must not block.
			Assert.False(mesh!.SegmentBlocked(new Vector3(0, 5, 5), new Vector3(9, 5, 5)));
		}

		[Fact]
		public void Segment_Through_The_Wall_Is_Blocked()
		{
			RdbCollisionMesh.TryParse(Wall(), out var mesh);
			Assert.True(mesh!.SegmentBlocked(new Vector3(0, 5, 5), new Vector3(20, 5, 5))); // crosses X=10 inside the quad
		}

		[Fact]
		public void Skips_A_Spurious_Magic_And_Still_Parses_The_Real_Mesh()
		{
			// A stray NXS\x01MESH in junk bytes (bad counts) must be skipped without derailing the real actor after it.
			var junk = new List<byte>();
			junk.AddRange([0x4E, 0x58, 0x53, 0x01, 0x4D, 0x45, 0x53, 0x48]);
			junk.AddRange(new byte[4]); // truncated - no valid nVerts/nTris reachable
			junk.AddRange(Wall());
			Assert.True(RdbCollisionMesh.TryParse(junk.ToArray(), out var mesh));
			Assert.Equal(2, mesh.TriangleCount);
		}

		// One cooked-mesh actor: [NXS\x01MESH][20 header bytes][u32 nVerts][u32 nTris][verts vec3 f32][tris u8 (nVerts<=256)].
		private static byte[] Actor(Vector3[] verts, (int, int, int)[] tris)
		{
			var b = new List<byte>();
			b.AddRange([0x4E, 0x58, 0x53, 0x01, 0x4D, 0x45, 0x53, 0x48]); // NXS\x01 + MESH
			b.AddRange(new byte[20]); // ver/flags/3 header words (unused by us)
			b.AddRange(BitConverter.GetBytes((uint)verts.Length));
			b.AddRange(BitConverter.GetBytes((uint)tris.Length));
			foreach (var v in verts)
			{
				b.AddRange(BitConverter.GetBytes(v.X));
				b.AddRange(BitConverter.GetBytes(v.Y));
				b.AddRange(BitConverter.GetBytes(v.Z));
			}

			foreach (var (i, j, k) in tris)
			{
				b.Add((byte)i);
				b.Add((byte)j);
				b.Add((byte)k);
			}

			return b.ToArray();
		}

		// A vertical wall quad at X=10, spanning Y[0,10] Z[0,10], as two triangles.
		private static byte[] Wall()
		{
			var verts = new[] { new Vector3(10, 0, 0), new Vector3(10, 10, 0), new Vector3(10, 0, 10), new Vector3(10, 10, 10) };
			return Actor(verts, [(0, 1, 2), (1, 3, 2)]);
		}
	}
}
