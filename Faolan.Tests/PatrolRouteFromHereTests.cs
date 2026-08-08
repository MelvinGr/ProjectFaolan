using System.Numerics;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// #716: a patrol body is anchored at a POSITION, so what gets handed to the client has to be the part of the beat
	// still ahead of the NPC, starting where it now stands. Re-issuing the captured body - anchored at the entity's
	// SPAWN - is what made a patroller teleport back to the start of its route the moment a lap ended.
	public class PatrolRouteFromHereTests
	{
		private static readonly Vector3[] Street =
		[
			new(0, 0, 0), new(0, 0, 10), new(0, 0, 20), new(0, 0, 30)
		];

		[Fact]
		public void MidBeat_HandsOverOnlyWhatIsStillAhead()
		{
			// Heading for waypoint 2 of an open route: the client is told about 2 and 3, not the two already walked.
			var ahead = PatrolRoute.FromHere(Street, 2, backwards: false, loops: false);

			Assert.Equal([Street[2], Street[3]], ahead);
		}

		[Fact]
		public void WalkingBack_HandsOverTheWaypointsInDescendingOrder()
		{
			// An open route is walked back DOWN its own polyline (#703), so the remainder runs the other way.
			var ahead = PatrolRoute.FromHere(Street, 1, backwards: true, loops: false);

			Assert.Equal([Street[1], Street[0]], ahead);
		}

		[Fact]
		public void ClosedRoute_CarriesOnThroughTheStart_SoALoopStaysALoop()
		{
			// The whole point of a loop is that it does not stop at the end of the list. From waypoint 2 the client
			// must be given 2, 3 and then 0, 1 - a full lap ending back where it picked up.
			var ahead = PatrolRoute.FromHere(Street, 2, backwards: false, loops: true);

			Assert.Equal([Street[2], Street[3], Street[0], Street[1]], ahead);
		}

		[Fact]
		public void ClosedRoute_AtTheStart_IsExactlyOneLap_NotTwo()
		{
			// The wrap must not re-add what the first pass already covered: standing at index 0, one lap IS the list.
			var ahead = PatrolRoute.FromHere(Street, 0, backwards: false, loops: true);

			Assert.Equal(Street, ahead);
		}

		[Fact]
		public void EmptyRoute_IsEmpty_RatherThanThrowing()
		{
			// A spawn whose harvested route came back empty must not take the caller down with it - it falls back.
			Assert.Empty(PatrolRoute.FromHere([], 0, backwards: false, loops: false));
		}
	}
}
