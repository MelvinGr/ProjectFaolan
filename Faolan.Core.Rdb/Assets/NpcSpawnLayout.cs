using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// The per-playfield NPC spawn layout (RDB type 1010506 = "NPC roam/movement data") - the capture-INDEPENDENT
	// authoritative open-world spawn source (#332). Decoded + validated byte-exact against the live client: all 238
	// LOCAL playfield records parse to EOF; 119 carry spawn groups (2,664 groups / 23,782 primary points), 119 are
	// empty (`groupCount == 0`) = scripted/instanced maps like the castle and the Tortage interiors, whose NPCs are
	// server-scripted and never appear here. The RDB record id == the playfield id (== Map.Id).
	//
	// Grammar (little-endian throughout; strings are [u16 len][latin1 bytes], floats are LE):
	//   record = [u32 version=1][u32 groupCount] groupCount*group
	//   group  = [str name][str category][str id]
	//            [u32 pointCount][vec3]*pointCount          <- the PRIMARY spawn points (what a placement sits on)
	//            [vec3 boundsMin][vec3 boundsMax]           <- an AABB over the group (max.Y is padded to min.Y + 10)
	//            [u32 pathCount] pathCount*([u32 n][vec3]*n) <- the roam/patrol paths the group's NPCs walk
	//   vec3   = [f32 x][f32 y][f32 z]
	// There is NO 12-byte RDB-Id header on this type: the payload begins at the [u32 version] word.
	//
	// The `category` selects what the `id` string means: "Community"/"Race"/"Superfamily" -> a numeric faction/family
	// id; "Name" -> the creature's display name + a 4-char tag (e.g. "Shark (H8AV)"); "no category" -> the literal
	// "no category value". So the id is a spawn-GROUP/community key, NOT (except for "Name" groups) the creature
	// TEMPLATE - the creature type still has to be correlated from captures (the #332 match/snap follow-up). We keep
	// the raw id string plus a parsed numeric CommunityId when it is numeric.
	public sealed class NpcSpawnLayout
	{
		public const uint ResourceType = 1010506;

		private NpcSpawnLayout(IReadOnlyList<SpawnGroup> groups)
		{
			Groups = groups;
		}

		public IReadOnlyList<SpawnGroup> Groups { get; }

		// Parses a decompressed 1010506 payload (the bytes RdbReader.ReadDecompressed returns for this type). Returns
		// false on any malformed field or if the parse doesn't consume the record EXACTLY to its end - the trailing
		// EOF check is what makes a mis-sized field surface as a clean failure instead of silently mis-reading.
		public static bool TryParse(ReadOnlySpan<byte> data, out NpcSpawnLayout? layout)
		{
			layout = null;
			var o = 0;
			if (!ReadU32(data, ref o, out var version) || version != 1)
				return false;
			if (!ReadU32(data, ref o, out var groupCount) || groupCount > 100000)
				return false;

			var groups = new List<SpawnGroup>((int)groupCount);
			for (var g = 0; g < groupCount; g++)
			{
				if (!ReadStr(data, ref o, out var name) ||
				    !ReadStr(data, ref o, out var category) ||
				    !ReadStr(data, ref o, out var id) ||
				    !ReadU32(data, ref o, out var pointCount) || pointCount > 1000000)
					return false;

				var points = new List<Vector3>((int)pointCount);
				for (var i = 0; i < pointCount; i++)
				{
					if (!ReadVec3(data, ref o, out var pt))
						return false;

					points.Add(pt);
				}

				if (!ReadVec3(data, ref o, out var boundsMin) || !ReadVec3(data, ref o, out var boundsMax))
					return false;

				if (!ReadU32(data, ref o, out var pathCount) || pathCount > 1000000)
					return false;

				var paths = new List<IReadOnlyList<Vector3>>((int)pathCount);
				for (var p = 0; p < pathCount; p++)
				{
					if (!ReadU32(data, ref o, out var n) || n > 1000000)
						return false;

					var path = new List<Vector3>((int)n);
					for (var i = 0; i < n; i++)
					{
						if (!ReadVec3(data, ref o, out var pt))
							return false;

						path.Add(pt);
					}

					paths.Add(path);
				}

				var communityId = uint.TryParse(id, out var cid)
					? cid
					: (uint?)null;
				groups.Add(new SpawnGroup(name!, category!, id!, communityId, points, boundsMin, boundsMax, paths));
			}

			if (o != data.Length)
				return false; // the grammar must consume the record exactly - a leftover tail means a mis-decode

			layout = new NpcSpawnLayout(groups);
			return true;
		}

		private static bool ReadU32(ReadOnlySpan<byte> b, ref int o, out uint v)
		{
			if (o + 4 > b.Length)
			{
				v = 0;
				return false;
			}

			v = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4));
			o += 4;
			return true;
		}

		private static bool ReadVec3(ReadOnlySpan<byte> b, ref int o, out Vector3 v)
		{
			if (o + 12 > b.Length)
			{
				v = default;
				return false;
			}

			v = new Vector3(
				BinaryPrimitives.ReadSingleLittleEndian(b.Slice(o, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(b.Slice(o + 4, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(b.Slice(o + 8, 4)));
			o += 12;
			return true;
		}

		private static bool ReadStr(ReadOnlySpan<byte> b, ref int o, out string? s)
		{
			s = null;
			if (o + 2 > b.Length)
				return false;

			int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o, 2));
			o += 2;
			if (o + len > b.Length)
				return false;

			s = Encoding.Latin1.GetString(b.Slice(o, len));
			o += len;
			return true;
		}

		// One named spawn group: its primary spawn points, its AABB, and the roam paths its NPCs patrol.
		public sealed record SpawnGroup(
			string Name,
			string Category,
			string Id,
			uint? CommunityId,
			IReadOnlyList<Vector3> SpawnPoints,
			Vector3 BoundsMin,
			Vector3 BoundsMax,
			IReadOnlyList<IReadOnlyList<Vector3>> RoamPaths);
	}
}
