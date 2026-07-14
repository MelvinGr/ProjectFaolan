using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// The RDB 1010506 ("NPC roam/movement") open-world spawn-layout parser (#332). The client RDB isn't in the repo,
	// so - like RdbCollisionMeshTests / RdbHeightmapTests - these build a SYNTHETIC record in the exact wire grammar
	// and lock the parse. The synthetic record mirrors the real Tarantia Noble District (2010) shape: a numeric
	// "Community" group with a roam path, plus a "Name" group whose id carries the creature name+tag.
	public class NpcSpawnLayoutTests
	{
		// Builds a 1010506 record byte-for-byte in the decoded grammar:
		//   [u32 version][u32 groupCount] then per group [str name][str category][str id][u32 pts][vec3]*
		//   [vec3 min][vec3 max][u32 paths] paths*([u32 n][vec3]*n).
		private static byte[] BuildRecord(uint version, params Group[] groups)
		{
			var b = new List<byte>();
			U32(b, version);
			U32(b, (uint)groups.Length);
			foreach (var g in groups)
			{
				Str(b, g.Name);
				Str(b, g.Category);
				Str(b, g.Id);
				U32(b, (uint)g.Points.Length);
				foreach (var p in g.Points)
					Vec3(b, p);

				Vec3(b, g.Min);
				Vec3(b, g.Max);
				U32(b, (uint)g.Paths.Length);
				foreach (var path in g.Paths)
				{
					U32(b, (uint)path.Length);
					foreach (var p in path)
						Vec3(b, p);
				}
			}

			return b.ToArray();
		}

		private static void U32(List<byte> b, uint v) => b.AddRange(System.BitConverter.GetBytes(v));
		private static void F32(List<byte> b, float v) => b.AddRange(System.BitConverter.GetBytes(v));

		private static void Str(List<byte> b, string s)
		{
			var bytes = Encoding.Latin1.GetBytes(s);
			b.AddRange(System.BitConverter.GetBytes((ushort)bytes.Length));
			b.AddRange(bytes);
		}

		private static void Vec3(List<byte> b, Vector3 v)
		{
			F32(b, v.X);
			F32(b, v.Y);
			F32(b, v.Z);
		}

		private static byte[] SampleRecord()
		{
			var brawlers = new Group
			{
				Name = "Brawlers",
				Category = "Community",
				Id = "295",
				Points = [new Vector3(782.692f, 176.560f, 986.754f), new Vector3(791.474f, 176.560f, 987.885f)],
				Min = new Vector3(782.692f, 176.560f, 986.754f),
				Max = new Vector3(791.474f, 186.560f, 987.885f),
				Paths = [[new Vector3(786.720f, 176.391f, 988.088f), new Vector3(784.914f, 176.391f, 988.242f), new Vector3(787.829f, 176.410f, 991.150f)]]
			};
			var sharks = new Group
			{
				Name = "Sharks",
				Category = "Name",
				Id = "Shark (H8AV)",
				Points = [new Vector3(100.0f, 5.0f, 200.0f)],
				Min = new Vector3(100.0f, 5.0f, 200.0f),
				Max = new Vector3(100.0f, 15.0f, 200.0f),
				Paths = [] // a stationary group has no roam paths
			};
			return BuildRecord(1, brawlers, sharks);
		}

		[Fact]
		public void Parses_Groups_Points_Bounds_And_Paths()
		{
			Assert.True(NpcSpawnLayout.TryParse(SampleRecord(), out var layout));
			Assert.NotNull(layout);
			Assert.Equal(2, layout!.Groups.Count);

			var brawlers = layout.Groups[0];
			Assert.Equal("Brawlers", brawlers.Name);
			Assert.Equal("Community", brawlers.Category);
			Assert.Equal("295", brawlers.Id);
			Assert.Equal(295u, brawlers.CommunityId);
			Assert.Equal(2, brawlers.SpawnPoints.Count);
			Assert.Equal(new Vector3(782.692f, 176.560f, 986.754f), brawlers.SpawnPoints[0]);
			Assert.Equal(new Vector3(791.474f, 186.560f, 987.885f), brawlers.BoundsMax);
			Assert.Single(brawlers.RoamPaths);
			Assert.Equal(3, brawlers.RoamPaths[0].Count);
			Assert.Equal(new Vector3(787.829f, 176.410f, 991.150f), brawlers.RoamPaths[0][2]);
		}

		[Fact]
		public void Name_Group_Keeps_Raw_Id_And_Has_No_Numeric_CommunityId()
		{
			NpcSpawnLayout.TryParse(SampleRecord(), out var layout);
			var sharks = layout!.Groups[1];
			Assert.Equal("Name", sharks.Category);
			Assert.Equal("Shark (H8AV)", sharks.Id); // the creature name+tag, not a number
			Assert.Null(sharks.CommunityId);
			Assert.Single(sharks.SpawnPoints);
			Assert.Empty(sharks.RoamPaths);
		}

		[Fact]
		public void Empty_Record_Parses_To_Zero_Groups()
		{
			// version=1, groupCount=0 - the shape of a scripted/instanced playfield (castle, Tortage interiors).
			Assert.True(NpcSpawnLayout.TryParse(BuildRecord(1), out var layout));
			Assert.Empty(layout!.Groups);
		}

		[Fact]
		public void Trailing_Garbage_Fails_The_Eof_Check()
		{
			var buf = new List<byte>(SampleRecord()) { 0x00 }; // one extra byte past the record end
			Assert.False(NpcSpawnLayout.TryParse(buf.ToArray(), out _));
		}

		[Fact]
		public void Wrong_Version_Is_Rejected()
		{
			Assert.False(NpcSpawnLayout.TryParse(BuildRecord(2), out _));
		}

		[Fact]
		public void Truncated_Record_Fails()
		{
			var full = SampleRecord();
			Assert.False(NpcSpawnLayout.TryParse(full.AsSpan(0, full.Length - 4), out _));
		}

		private sealed class Group
		{
			public string Name = "";
			public string Category = "";
			public string Id = "";
			public Vector3[] Points = [];
			public Vector3 Min;
			public Vector3 Max;
			public Vector3[][] Paths = [];
		}
	}
}
