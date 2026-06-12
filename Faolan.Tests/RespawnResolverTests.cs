using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Respawn-point selection (docs/archive/server-architecture-roadmap.md): a dead player respawns at the CLOSEST point they
	// know - an always-known starter or one they've discovered.
	public class RespawnResolverTests
	{
		[Fact]
		public void ClosestKnown_AlwaysKnownNeedsNoDiscovery()
		{
			var beach = Point(1, 5, 0, true);
			var target = RespawnResolver.ClosestKnown(Vector3.Zero, new[] { beach }, null);
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
			Assert.Null(RespawnResolver.ClosestKnown(Vector3.Zero, new[] { unknown }, new HashSet<uint>()));
			Assert.Null(RespawnResolver.ClosestKnown(Vector3.Zero, Array.Empty<RespawnPoint>(), null));
		}

		[Fact]
		public void ClosestKnown_PicksNearestKnownPoint()
		{
			var beach = Point(1, 0, 0, true);
			var nearDiscovered = Point(2, 10, 0);
			var points = new[] { beach, nearDiscovered };
			var discovered = new HashSet<uint> { 2 };

			// Death at (12,0,0): the discovered point (id 2, dist 2) is closer than the beach (dist 12).
			var target = RespawnResolver.ClosestKnown(new Vector3(12, 0, 0), points, discovered);
			Assert.Same(nearDiscovered, target);
		}

		private static RespawnPoint Point(uint id, float x, float z, bool alwaysKnown = false)
		{
			return new RespawnPoint
			{
				Id = id,
				MapId = 4010,
				AlwaysKnown = alwaysKnown,
				Position = new Vector3(x, 0, z)
			};
		}
	}
}
