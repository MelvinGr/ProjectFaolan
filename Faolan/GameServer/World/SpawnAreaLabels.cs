using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.World
{
	// #363 dev nameplate labelling for NPC spawn AREAS - which area a spawn belongs to, and how to write that area's
	// name on an overhead nameplate. Pure functions over the shipped `NpcSpawnArea` rows; no world state, no wire.
	//
	// Shared by the two populations that both want the label, which is why it is its own type rather than a private
	// helper on either: the captured `NpcSpawn` load (`EnsureZoneNpcs`, via `AreaLabelFor`) and the ambient pools
	// (`AmbientPoolService`, via `FormatAreaLabel`). Only rendered when the GM toggles `.togglenpcgroup`.
	public static class SpawnAreaLabels
	{
		// The spawn area a position lands in (nearest by centroid when several overlap; bounds padded), or null.
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

		// #363 dev label for a captured spawn's nameplate: LINK-first - the area it's a confirmed MEMBER of
		// (NpcSpawnAreaId), even if it sits outside the bounds (a hand-linked stray); else the area its POSITION lands in
		// (so an unlinked NPC still previews which area it's in). Null when neither. See FormatAreaLabel.
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

		// The nameplate label for an area: "<group> | <tier> x<count>" (e.g. "Ape2 | id-prop x8"). ASCII-only on
		// purpose (#363): the client's nameplate font doesn't render the "·"/"×" glyphs, and our own SpawnBodyReader
		// rejects a non-ASCII overhead name - both broke the dev label silently, so keep it 7-bit.
		public static string FormatAreaLabel(NpcSpawnArea a)
		{
			return $"{a.GroupName} | {ResolvedTag(a.ResolvedBy)} x{a.MaxCount}";
		}

		// Short nameplate-friendly tag for a spawn area's creature-resolution provenance (#363).
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
