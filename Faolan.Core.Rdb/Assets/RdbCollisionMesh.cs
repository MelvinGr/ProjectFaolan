using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	// The per-playfield collision geometry (RDB type 1000012 = `NXUSTREAM`, NovodeX/Ageia PhysX cooked triangle
	// meshes) decoded into WORLD-space triangles for server-side line-of-sight (#284 tier-3: buildings/walls that the
	// terrain heightmap can't represent). The stream is a back-to-back run of `NXS\x01MESH` cooked-mesh actors; each:
	//   [NXS\x01][MESH][u32 ver][u32 flags][f32][u32][u32][u32 nVerts][u32 nTris][nVerts * vec3 f32][nTris * 3 index]
	// then a per-mesh Ice/RTree BVH we skip (we build our own grid). Index width follows the vertex count (1B if
	// nVerts<=256, else 2B, else 4B). The vertices are already in world coordinates (per-mesh centroids span the whole
	// playfield - no actor transform to apply), so a triangle can be tested directly against a world-space segment.
	// Validated: beach 4010 = 666 actors / 116,178 tris, city 4020 = 1,518 / 195,763, 0 bad-parse.
	public sealed class RdbCollisionMesh
	{
		public const uint ResourceType = 1000012;
		private const float CellSize = 16f; // world units per spatial-grid cell (X/Z); ~a few tris per short segment

		private static readonly byte[] MeshMagic = [0x4E, 0x58, 0x53, 0x01, 0x4D, 0x45, 0x53, 0x48]; // NXS\x01 + MESH
		private readonly Dictionary<long, List<int>> _grid; // (cellX,cellZ) packed -> triangle numbers overlapping it

		private readonly float[] _verts; // flat x,y,z
		private readonly int[] _tris; // flat vertex indices (3 per triangle)

		public int TriangleCount => _tris.Length / 3;

		private RdbCollisionMesh(float[] verts, int[] tris, Dictionary<long, List<int>> grid)
		{
			_verts = verts;
			_tris = tris;
			_grid = grid;
		}

		// True if collision geometry blocks the straight segment a->b (an NPC's eye to a player). Tests the triangles in
		// the grid cells the segment's X/Z footprint touches.
		public bool SegmentBlocked(Vector3 a, Vector3 b)
		{
			var minGx = Cell(MathF.Min(a.X, b.X));
			var maxGx = Cell(MathF.Max(a.X, b.X));
			var minGz = Cell(MathF.Min(a.Z, b.Z));
			var maxGz = Cell(MathF.Max(a.Z, b.Z));

			var dir = b - a;
			var seen = new HashSet<int>();
			for (var gx = minGx; gx <= maxGx; gx++)
			for (var gz = minGz; gz <= maxGz; gz++)
			{
				if (!_grid.TryGetValue(Key(gx, gz), out var list))
					continue;

				foreach (var t in list)
				{
					if (!seen.Add(t))
						continue;

					var (aX, aY, aZ) = V(_verts, _tris[t * 3]);
					var (bX, bY, bZ) = V(_verts, _tris[t * 3 + 1]);
					var (cX, cY, cZ) = V(_verts, _tris[t * 3 + 2]);
					if (SegmentHitsTriangle(a, dir, new Vector3(aX, aY, aZ), new Vector3(bX, bY, bZ), new Vector3(cX, cY, cZ)))
						return true;
				}
			}

			return false;
		}

		// Parse an NXUSTREAM (type 1000012) body into world triangles + a 2D X/Z grid index. Returns false only on an
		// empty/garbage stream (a map with no collision).
		public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out RdbCollisionMesh? mesh)
		{
			mesh = null;
			var verts = new List<float>();
			var tris = new List<int>();

			var pos = 0;
			while (true)
			{
				var at = IndexOf(body, MeshMagic, pos);
				if (at < 0)
					break;

				pos = at + MeshMagic.Length;
				if (!TryReadMesh(body, at, verts, tris))
					continue; // a spurious magic in the BVH bytes - skip, the next real header still parses
			}

			if (tris.Count == 0)
				return false;

			var vertArr = verts.ToArray();
			var triArr = tris.ToArray();
			mesh = new RdbCollisionMesh(vertArr, triArr, BuildGrid(vertArr, triArr));
			return true;
		}

		private static Dictionary<long, List<int>> BuildGrid(float[] verts, int[] tris)
		{
			var grid = new Dictionary<long, List<int>>();
			for (var t = 0; t < tris.Length / 3; t++)
			{
				var (aX, _, aZ) = V(verts, tris[t * 3]);
				var (bX, _, bZ) = V(verts, tris[t * 3 + 1]);
				var (cX, _, cZ) = V(verts, tris[t * 3 + 2]);
				var minGx = Cell(MathF.Min(aX, MathF.Min(bX, cX)));
				var maxGx = Cell(MathF.Max(aX, MathF.Max(bX, cX)));
				var minGz = Cell(MathF.Min(aZ, MathF.Min(bZ, cZ)));
				var maxGz = Cell(MathF.Max(aZ, MathF.Max(bZ, cZ)));
				for (var gx = minGx; gx <= maxGx; gx++)
				for (var gz = minGz; gz <= maxGz; gz++)
				{
					var key = Key(gx, gz);
					if (!grid.TryGetValue(key, out var list))
						grid[key] = list = [];
					list.Add(t);
				}
			}

			return grid;
		}

		private static int Cell(float world)
		{
			return (int)MathF.Floor(world / CellSize);
		}

		private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int start)
		{
			if (start < 0)
				start = 0;
			var idx = haystack[start..].IndexOf(needle);
			return idx < 0
				? -1
				: start + idx;
		}

		private static long Key(int gx, int gz)
		{
			return ((long)gx << 32) ^ (uint)gz;
		}

		// Möller-Trumbore, clamped to the segment (0 <= t <= 1 along `dir` = b - a).
		private static bool SegmentHitsTriangle(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2)
		{
			const float eps = 1e-6f;
			var edge1 = v1 - v0;
			var edge2 = v2 - v0;
			var pvec = Vector3.Cross(dir, edge2);
			var det = Vector3.Dot(edge1, pvec);
			if (det is > -eps and < eps)
				return false; // parallel

			var inv = 1f / det;
			var tvec = origin - v0;
			var u = Vector3.Dot(tvec, pvec) * inv;
			if (u < 0f || u > 1f)
				return false;

			var qvec = Vector3.Cross(tvec, edge1);
			var v = Vector3.Dot(dir, qvec) * inv;
			if (v < 0f || u + v > 1f)
				return false;

			var t = Vector3.Dot(edge2, qvec) * inv;
			return t is >= 0f and <= 1f; // within the segment
		}

		private static bool TryReadMesh(ReadOnlySpan<byte> body, int magicAt, List<float> verts, List<int> tris)
		{
			// nVerts / nTris sit 28 / 32 bytes past the NXS magic (ver, flags, and three header words in between).
			var vCountOff = magicAt + 28;
			if (vCountOff + 8 > body.Length)
				return false;

			var nVerts = (int)body.ReadU32Le(vCountOff);
			var nTris = (int)body.ReadU32Le(vCountOff + 4);
			if (nVerts <= 0 || nTris <= 0 || nVerts > 5_000_000 || nTris > 10_000_000)
				return false;

			var vStart = vCountOff + 8;
			var vBytes = (long)nVerts * 12;
			if (vStart + vBytes > body.Length)
				return false;

			var baseVert = verts.Count / 3;
			var vSpan = body.Slice(vStart, (int)vBytes);
			for (var i = 0; i < nVerts * 3; i++)
				verts.Add(vSpan.ReadF32Le(i * 4));

			// Index width = the smallest that indexes nVerts (matches the PhysX cooker).
			var width = nVerts <= 256
				? 1
				: nVerts <= 65536
					? 2
					: 4;
			var tStart = (int)(vStart + vBytes);
			var tBytes = (long)nTris * 3 * width;
			if (tStart + tBytes > body.Length)
			{
				verts.RemoveRange(baseVert * 3, nVerts * 3); // roll back the verts we added for this bad mesh
				return false;
			}

			for (var i = 0; i < nTris * 3; i++)
			{
				var o = tStart + i * width;
				var idx = width switch
				{
					1 => body[o],
					2 => body.ReadU16Le(o),
					_ => (int)body.ReadU32Le(o)
				};

				if (idx >= nVerts)
				{
					// wrong width guess for this mesh - roll back and skip it
					verts.RemoveRange(baseVert * 3, nVerts * 3);
					tris.RemoveRange(tris.Count - i, i);
					return false;
				}

				tris.Add(baseVert + idx);
			}

			return true;
		}

		private static (float X, float Y, float Z) V(float[] verts, int i)
		{
			return (verts[i * 3], verts[i * 3 + 1], verts[i * 3 + 2]);
		}
	}
}
