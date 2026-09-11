using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Developer nameplate labelling for NPC spawn areas: which area a spawn belongs to, and how to write that
	///     area's name on an overhead nameplate. Pure functions over the shipped <c>NpcSpawnArea</c> rows, with no
	///     world state and no wire.
	/// </summary>
	/// <remarks>
	///     Its own type because the two populations that want the label share it: the captured <c>NpcSpawn</c> load
	///     through <see cref="AreaLabelFor" />, and the ambient pools through <see cref="FormatAreaLabel" />. Only
	///     rendered when a GM toggles it on.
	/// </remarks>
	public static class SpawnAreaLabels
	{
		/// <summary>
		///     The spawn area a position lands in - nearest by centroid when several overlap, with padded bounds - or
		///     null.
		/// </summary>
		public static NpcSpawnArea? AreaContaining(IReadOnlyList<NpcSpawnArea> areas, Vector3 pos)
		{
			const float pad = 5f;
			NpcSpawnArea? best = null;
			var bestDistSq = float.MaxValue;
			foreach (var a in areas)
			{
				if (pos.X < a.BoundsMinX - pad || pos.X > a.BoundsMaxX + pad || pos.Z < a.BoundsMinZ - pad || pos.Z > a.BoundsMaxZ + pad)
					continue;

				var cx = (a.BoundsMinX + a.BoundsMaxX) * 0.5f;
				var cz = (a.BoundsMinZ + a.BoundsMaxZ) * 0.5f;
				var distSq = (pos.X - cx) * (pos.X - cx) + (pos.Z - cz) * (pos.Z - cz);
				if (distSq < bestDistSq)
				{
					bestDistSq = distSq;
					best = a;
				}
			}

			return best;
		}

		/// <summary>
		///     The developer label for a captured spawn's nameplate, link first: the area it is a confirmed member of
		///     (<c>NpcSpawnAreaId</c>) even when it sits outside the bounds, as a hand-linked stray does, else the area
		///     its position lands in, so an unlinked NPC still previews which area it is in. Null when neither answers.
		/// </summary>
		public static string? AreaLabelFor(IReadOnlyList<NpcSpawnArea> areas, NpcSpawn npc)
		{
			NpcSpawnArea? area = null;
			if (npc.NpcSpawnAreaId is { } id)
				area = areas.FirstOrDefault(a => a.Id == id);

			area ??= AreaContaining(areas, npc.Position);
			return area == null
				? null
				: FormatAreaLabel(area);
		}

		/// <summary>
		///     The nameplate label for an area: <c>&lt;group&gt; | &lt;tier&gt; x&lt;count&gt;</c>, for instance
		///     "Ape2 | id-prop x8".
		/// </summary>
		/// <remarks>
		///     ASCII-only on purpose: the client's nameplate font does not render the middle-dot or multiplication
		///     glyphs, and the spawn body reader rejects a non-ASCII overhead name - both fail silently, so keep it
		///     7-bit.
		/// </remarks>
		public static string FormatAreaLabel(NpcSpawnArea a)
		{
			return $"{a.GroupName} | {ResolvedTag(a.ResolvedBy)} x{a.MaxCount}";
		}

		// A short nameplate-friendly tag for a spawn area's creature-resolution provenance.
		private static string ResolvedTag(SpawnResolutionSource resolvedBy)
		{
			return resolvedBy switch
			{
				SpawnResolutionSource.Name          => "name",
				SpawnResolutionSource.Containment   => "contain",
				SpawnResolutionSource.IdPropagation => "id-prop",
				SpawnResolutionSource.Manual        => "manual",
				_                                   => "unresolved"
			};
		}
	}
}
