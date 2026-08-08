using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// #713: two sightings of one patroller must resolve to the SAME placement, and two genuinely different beats must
	// not. The key is what stops Old Tarantia standing up two Noblemen on one street - and, just as importantly, what
	// stops a real pair of NPCs being collapsed into one.
	public class PatrolPlacementKeyTests
	{
		[Fact]
		public void TwoSightingsOfOneBeat_ShareAPlacement()
		{
			// The same route, harvested twice - the NPC was simply somewhere else along it the second time, which is
			// what made the importer's MapId+XYZ key mint two placements.
			Assert.Equal(Key(Route(0, 0, 10, 0, 20, 0), 14164), Key(Route(0, 0, 10, 0, 20, 0), 14164));
		}

		[Fact]
		public void ADifferentBeat_IsADifferentPlacement()
		{
			Assert.NotEqual(Key(Route(0, 0, 10, 0, 20, 0), 14164), Key(Route(0, 0, 10, 0, 20, 5), 14164));
		}

		[Fact]
		public void TheSameBeatWalkedByADifferentTYPE_IsADifferentPlacement()
		{
			// Two creatures given the same path are two placements, so the type is part of the key.
			Assert.NotEqual(Key(Route(0, 0, 10, 0, 20, 0), 14164), Key(Route(0, 0, 10, 0, 20, 0), 14231));
		}

		[Fact]
		public void AFloatWobbleInTheLastBIT_DoesNotSplitOnePlacement()
		{
			// The two harvests come from the same server-side path, but nothing guarantees bit-identical floats -
			// and a key that split on the last bit would silently stop deduplicating.
			var a = Key(Route(0, 0, 10, 0, 20, 0), 14164);
			var b = Key(Route(0, 0, 10.0000001f, 0, 20, 0), 14164);
			Assert.Equal(a, b);
		}

		[Fact]
		public void ASpawnWithNoRoute_HasNoPlacement()
		{
			// A stationary NPC cannot be identified this way, and must NOT collapse with every other routeless spawn
			// into one shared null-ish bucket - the caller renders it verbatim instead.
			Assert.Null(PatrolRoute.PlacementKey([], 14164));
		}

		private static uint? Key(ICollection<PatrolNode> nodes, uint templateId)
		{
			return PatrolRoute.PlacementKey(nodes, templateId);
		}

		// One mode-1 path node carrying the given x/z pairs as waypoints.
		private static List<PatrolNode> Route(params float[] xz)
		{
			var node = new PatrolNode { Mode = 1, OrdinalPath = "", ParentPath = null };
			for (var i = 0; i < xz.Length; i += 2)
			{
				node.Waypoints.Add(new PatrolNodeWaypoint
				{
					Ordinal = i / 2,
					PositionX = xz[i],
					PositionY = 0f,
					PositionZ = xz[i + 1]
				});
			}

			return [node];
		}
	}
}
