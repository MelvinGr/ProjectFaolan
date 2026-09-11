using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// A harvested route must be one continuous polyline, because the runtime walks it - a break is not skipped,
	// it is walked as a straight line between the two sides, through whatever stands there (Old Tarantia's guards
	// crossing the buildings).
	//
	// A captured mode-4 ListMoveTask routinely holds two mode-1 legs: the NPC's current short step, then the beat it
	// is walking. Over the corpus every multi-leg task has exactly two legs, and the leading one is 2 waypoints in 450
	// of 465 cases. Usually the beat starts where the step ends (median seam 0.7u) and the two really are one path;
	// in 141 cases the beat is elsewhere (up to 495.9u), because a roam path is shared by a group and the NPC joins it
	// partway rather than at its first waypoint.
	public class PatrolRouteTrimTests
	{
		[Fact]
		public void ContinuousLegs_AreLeftAlone()
		{
			// The common case: the step ends where the beat begins, so the two are one path and nothing may be dropped.
			var task = List(
				Leg([new Vector3(0, 0, 0), new Vector3(10, 0, 0)]),
				Leg([new Vector3(10, 0, 0), new Vector3(20, 0, 0), new Vector3(20, 0, 10)]));

			PatrolPathExtractor.TrimToContinuousRoute(task);

			Assert.Equal(2, task.Children.Count);
			Assert.Equal(5, Waypoints(task).Count);
		}

		[Fact]
		public void DisjointStub_IsDropped_AndTheBeatKept()
		{
			// The reported shape: a 2-point step at the spawn, then a beat that starts 100u away. Concatenated, the
			// seam between them is a 100u straight line through the city.
			var task = List(
				Leg([new Vector3(0, 0, 0), new Vector3(3, 0, 0)]),
				Leg([new Vector3(103, 0, 0), new Vector3(120, 0, 0), new Vector3(120, 0, 20)]));

			PatrolPathExtractor.TrimToContinuousRoute(task);

			var kept = Waypoints(task);
			Assert.Single(task.Children);
			Assert.Equal(3, kept.Count);
			Assert.Equal(new Vector3(103, 0, 0), kept[0]); // the beat, not the stub
		}

		[Fact]
		public void EveryKeptSeam_JoinsWithinTheTolerance()
		{
			// The invariant itself, over a task with two breaks: whatever survives must be walkable end to end.
			var task = List(
				Leg([new Vector3(0, 0, 0), new Vector3(2, 0, 0)]),
				Leg([new Vector3(200, 0, 0), new Vector3(210, 0, 0), new Vector3(220, 0, 0), new Vector3(230, 0, 0)]),
				Leg([new Vector3(230, 0, 0), new Vector3(240, 0, 0)]),
				Leg([new Vector3(900, 0, 0), new Vector3(910, 0, 0)]));

			PatrolPathExtractor.TrimToContinuousRoute(task);

			var kept = Waypoints(task);
			Assert.Equal(6, kept.Count); // the middle run (4 + 2), the longest of the three
			for (var i = 1; i < kept.Count; i++)
				Assert.True(Vector3.Distance(kept[i - 1], kept[i]) <= 10.001f, $"a break survived at {kept[i]}");
		}

		[Fact]
		public void LongestRunWins_EvenWhenItIsNotTheFirst()
		{
			// Nothing privileges the leading leg: the beat is whichever continuous run carries the most waypoints.
			var task = List(
				Leg([new Vector3(0, 0, 0), new Vector3(5, 0, 0), new Vector3(10, 0, 0)]),
				Leg([new Vector3(500, 0, 0), new Vector3(510, 0, 0), new Vector3(520, 0, 0), new Vector3(530, 0, 0)]));

			PatrolPathExtractor.TrimToContinuousRoute(task);

			Assert.Single(task.Children);
			Assert.Equal(new Vector3(500, 0, 0), Waypoints(task)[0]);
		}

		[Fact]
		public void SingleLeg_IsContinuousByConstruction()
		{
			var task = List(Leg([new Vector3(0, 0, 0), new Vector3(40, 0, 0)]));

			PatrolPathExtractor.TrimToContinuousRoute(task);

			Assert.Single(task.Children);
			Assert.Equal(2, Waypoints(task).Count);
		}

		[Fact]
		public void TerritoryLeg_IsNotTouched()
		{
			// A PathFlags&1 leg names a territory path instead of carrying one: no geometry, so it can neither
			// break continuity nor be measured, and it must survive the trim intact.
			var task = List(
				new MoveTaskNode { Mode = 1, PathFlags = 1, TerritoryIndex = 7, TerritoryPathIndex = 2 },
				Leg([new Vector3(0, 0, 0), new Vector3(10, 0, 0)]));

			PatrolPathExtractor.TrimToContinuousRoute(task);

			Assert.Equal(2, task.Children.Count);
			Assert.Equal(7, task.Children[0].TerritoryIndex);
		}

		private static MoveTaskNode List(params MoveTaskNode[] children)
		{
			return new MoveTaskNode { Mode = 4, Children = [.. children] };
		}

		// A mode-1 PathMoveTask leg carrying an explicit waypoint list (3 raw big-endian float-bit u32 per waypoint).
		private static MoveTaskNode Leg(Vector3[] waypoints)
		{
			var node = new MoveTaskNode { Mode = 1 };
			foreach (var w in waypoints)
			{
				node.Waypoints.Add(BitConverter.SingleToUInt32Bits(w.X));
				node.Waypoints.Add(BitConverter.SingleToUInt32Bits(w.Y));
				node.Waypoints.Add(BitConverter.SingleToUInt32Bits(w.Z));
			}

			return node;
		}

		private static List<Vector3> Waypoints(MoveTaskNode task)
		{
			var all = new List<Vector3>();
			foreach (var child in task.Children)
			{
				for (var i = 0; i + 2 < child.Waypoints.Count; i += 3)
				{
					all.Add(new Vector3(
						BitConverter.UInt32BitsToSingle(child.Waypoints[i]),
						BitConverter.UInt32BitsToSingle(child.Waypoints[i + 1]),
						BitConverter.UInt32BitsToSingle(child.Waypoints[i + 2])));
				}
			}

			return all;
		}
	}
}
