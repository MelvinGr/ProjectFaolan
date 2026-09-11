using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     The per-playfield NPC spawn layout (RDB type 1010506, "NPC roam/movement data"): the capture-independent,
	///     authoritative open-world spawn source. The RDB record id is the playfield id (Map.Id). See
	///     docs/rdb/npc-spawn-layout-1010506.md.
	/// </summary>
	/// <remarks>
	///     All 238 local playfield records parse to EOF. 119 carry spawn groups (2,664 groups over 23,782 primary
	///     points) and 119 are empty (<c>groupCount == 0</c>) — the scripted and instanced maps such as the castle and
	///     the Tortage interiors, whose NPCs are server-scripted and never appear here.
	///     <para>
	///         The grammar is little-endian throughout; strings are [u16 len][latin1 bytes] and floats are
	///         little-endian:
	///         <code>
	///         record = [u32 version=1][u32 groupCount] groupCount*group
	///         group  = [str name][str category][str id]
	///                  [u32 pointCount][vec3]*pointCount           the primary spawn points a placement sits on
	///                  [vec3 boundsMin][vec3 boundsMax]            the group's roam area (AABB; max.Y padded to
	///                                                              min.Y + 10)
	///                  [u32 pathCount] pathCount*([u32 n][vec3]*n) wander samples filling the roam area, not
	///                                                              distinct routes
	///         vec3   = [f32 x][f32 y][f32 z]
	///         </code>
	///         There is no 12-byte RDB-Id header on this type: the payload begins at the [u32 version] word.
	///     </para>
	///     <para>
	///         The bounds pair is the roam area and the pathCount sub-lists are space-filling wander samples of that
	///         same box: path length over span is about 5 for every group, and all of a group's sub-lists share the
	///         decoded AABB to within a few units. Groups differ only in area size, not in structure.
	///     </para>
	///     <para>
	///         The category selects what the id string means. "Community", "Race" and "Superfamily" give a numeric
	///         faction or family id; "Name" gives the creature's display name plus a 4-character tag, such as
	///         "Shark (H8AV)"; "no category" gives the literal "no category value". So except on "Name" groups the id
	///         is a spawn-group or community key rather than the creature template. Both the raw id string and a
	///         parsed numeric <c>CommunityId</c> are kept.
	///     </para>
	/// </remarks>
	public sealed class NpcSpawnLayout
	{
		public const uint ResourceType = 1010506;

		public IReadOnlyList<SpawnGroup> Groups { get; }

		private NpcSpawnLayout(IReadOnlyList<SpawnGroup> groups)
		{
			Groups = groups;
		}

		/// <summary>
		///     Parses a decompressed 1010506 payload, the bytes <see cref="RdbReader.ReadDecompressed" /> returns for
		///     this type.
		/// </summary>
		/// <returns>
		///     False on any malformed field, or when the parse does not consume the record exactly to its end. That
		///     trailing EOF check is what makes a mis-sized field surface as a clean failure instead of a silent
		///     mis-read.
		/// </returns>
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
				if (!ReadStr(data, ref o, out var name) || !ReadStr(data, ref o, out var category) || !ReadStr(data, ref o, out var id) || !ReadU32(data, ref o, out var pointCount) ||
				    pointCount > 1000000)
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
				return false; // The grammar must consume the record exactly: a leftover tail means a mis-decode.

			layout = new NpcSpawnLayout(groups);
			return true;
		}

		private static bool ReadStr(ReadOnlySpan<byte> b, ref int o, out string? s)
		{
			s = null;
			if (o + 2 > b.Length)
				return false;

			int len = b.ReadU16Le(o);
			o += 2;
			if (o + len > b.Length)
				return false;

			s = Encoding.Latin1.GetString(b.Slice(o, len));
			o += len;
			return true;
		}

		private static bool ReadU32(ReadOnlySpan<byte> b, ref int o, out uint v)
		{
			if (o + 4 > b.Length)
			{
				v = 0;
				return false;
			}

			v = b.ReadU32Le(o);
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

			v = new Vector3(b.ReadF32Le(o), b.ReadF32Le(o + 4),
				b.ReadF32Le(o + 8));
			o += 12;
			return true;
		}

		/// <summary>
		///     One named spawn group: its primary spawn points, its roam area as the BoundsMin/BoundsMax AABB, and the
		///     wander-sample sub-lists in <c>RoamPaths</c> that fill that area — samples of the region, not distinct
		///     routes.
		/// </summary>
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
