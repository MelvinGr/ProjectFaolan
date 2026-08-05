using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Respawn-point selection (Gitea #404): a dead player respawns at the CLOSEST point they
	// know - an always-known starter or one they've discovered.
	public class RespawnResolverTests
	{
		[Fact]
		public void ClosestKnown_AlwaysKnownNeedsNoDiscovery()
		{
			var beach = Point(1, 5, 0, true);
			var target = RespawnResolver.ClosestKnown(Vector3.Zero, [beach], null!);
			Assert.Same(beach, target);
		}

		[Fact]
		public void ClosestKnown_IgnoresUndiscoveredPoints()
		{
			var beach = Point(1, 100, 0, true);
			var nearButUnknown = Point(2, 1, 0); // closer, but NOT discovered
			var points = new[] { beach, nearButUnknown };

			// Knows only the always-known beach -> respawns there despite the closer unknown point.
			var target = RespawnResolver.ClosestKnown(new Vector3(0, 0, 0), points, new HashSet<uint>());
			Assert.Same(beach, target);
		}

		[Fact]
		public void ClosestKnown_NullWhenNothingKnown()
		{
			var unknown = Point(1, 5, 0); // not always-known, not discovered
			Assert.Null(RespawnResolver.ClosestKnown(Vector3.Zero, [unknown], new HashSet<uint>()));
			Assert.Null(RespawnResolver.ClosestKnown(Vector3.Zero, [], null!));
		}

		[Fact]
		public void ClosestKnown_PicksNearestKnownPoint()
		{
			var beach = Point(1, 0, 0, true);
			var nearDiscovered = Point(2, 10, 0);
			var points = new[] { beach, nearDiscovered };
			var discovered = new HashSet<uint>
			{
				2
			};

			// Death at (12,0,0): the discovered point (id 2, dist 2) is closer than the beach (dist 12).
			var target = RespawnResolver.ClosestKnown(new Vector3(12, 0, 0), points, discovered);
			Assert.Same(nearDiscovered, target);
		}

		// #517: the outlaw split. Every shady point in the RDB is AlwaysKnown, so the only question a resolve
		// answers is WHICH half of the map's points a character is allowed to land in.
		[Fact]
		public void Resolve_CriminalTakesTheShadyPointEvenWhenANormalOneIsCloser()
		{
			var beach = Point(1, 0, 0, true);
			var outlawCamp = Point(2, 100, 0, true, shady: true);

			var target = RespawnResolver.Resolve(new Vector3(5, 0, 0), [beach, outlawCamp], new HashSet<uint>(), criminal: true);
			Assert.Same(outlawCamp, target);
		}

		[Fact]
		public void Resolve_NonCriminalNeverLandsInAnOutlawCamp()
		{
			var beach = Point(1, 100, 0, true);
			var outlawCamp = Point(2, 0, 0, true, shady: true); // closest by far, and always-known

			var target = RespawnResolver.Resolve(new Vector3(5, 0, 0), [beach, outlawCamp], new HashSet<uint>(), criminal: false);
			Assert.Same(beach, target);
		}

		[Fact]
		public void Resolve_CriminalFallsBackToTheNormalPointOnAMapWithNoShadyOne()
		{
			// 199 of the 248 mapped playfields carry no shady point, so this is the common case for a criminal.
			var beach = Point(1, 0, 0, true);

			var target = RespawnResolver.Resolve(new Vector3(5, 0, 0), [beach], new HashSet<uint>(), criminal: true);
			Assert.Same(beach, target);
		}

		[Fact]
		public void Resolve_FallsBackToAKnownPointAndThenToTheMapsFirst()
		{
			var unknown = Point(1, 5, 0);
			// Nothing known -> the map's first point rather than leaving the player with nowhere to go.
			Assert.Same(unknown, RespawnResolver.Resolve(Vector3.Zero, [unknown], new HashSet<uint>(), criminal: false));
			Assert.Null(RespawnResolver.Resolve(Vector3.Zero, [], new HashSet<uint>(), criminal: false));
		}

		private static RespawnPoint Point(uint id, float x, float z, bool alwaysKnown = false, bool shady = false)
		{
			return new RespawnPoint
			{
				Id = id,
				MapId = 4010,
				AlwaysKnown = alwaysKnown,
				Shady = shady,
				Position = new Vector3(x, 0, z)
			};
		}
	}
}
