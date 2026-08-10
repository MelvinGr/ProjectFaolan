using System;
using System.Numerics;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// #741: one NPC bound to ANOTHER NPC's movement - the ambient formation-follow the maintainer's "giner & scruffy
	// should be chasing each other" annotation names.
	//
	// The tick used to require the leader to be a PLAYER, which is what made the behaviour inexpressible: binding an
	// NPC leader cleared itself on the very next tick. These gates hold that open, and hold the wire form the corpus
	// states.
	public class NpcFormationFollowTests
	{
		// The wire form, from the capture: `[u32 class][u32 leaderHandle][f32 x][f32 y][f32 z][u32 slot]`. The offset
		// is a VECTOR, which is what the Black Dragon Guard formation proves - its four followers sit on a 3x3 grid -
		// and reading word 4 as a lone scalar was right for Scruffy and wrong for the 55 records displaced on X.
		[Fact]
		public void TheFollowTask_CarriesAnOffsetVectorAndASlot()
		{
			var node = MoveTaskNodes.Formation(0x00031C7A, new Vector3(1f, 0f, -3f), 6);

			Assert.Equal(MoveTaskNodes.ModeFormation, node.Mode);
			Assert.Equal(0x00031C7Au, node.Formation6[1]);
			Assert.Equal(1f, BitConverter.UInt32BitsToSingle(node.Formation6[2]));
			Assert.Equal(0f, BitConverter.UInt32BitsToSingle(node.Formation6[3]));
			Assert.Equal(-3f, BitConverter.UInt32BitsToSingle(node.Formation6[4]));
			Assert.Equal(6u, node.Formation6[5]);
		}

		// The behaviour itself: a follower bound to a stationary NPC leader walks to it and stays bound, rather than
		// having its binding cleared because the leader is not a player.
		[Fact]
		public void AFollowerWalksToItsNpcLeader_AndKeepsTheBinding()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var leader = Npc(1, 0, 0);
			var follower = Npc(2, 30, 0);
			follower.FollowTargetHandle = leader.Handle;
			zone.Add(leader);
			zone.Add(follower);

			var start = Vector3.Distance(follower.Position, leader.Position);
			for (var i = 0; i < 60; i++)
				world.Tick(0.5);

			Assert.Equal(leader.Handle, follower.FollowTargetHandle);
			Assert.True(Vector3.Distance(follower.Position, leader.Position) < start,
				"a formation follower should close on its NPC leader");
		}

		// It settles at its own FORMATION SLOT rather than inside the leader, the same fan-out a player's pets get -
		// which is what retail's four Black Dragon Guard Patrollers do behind one captain.
		[Fact]
		public void TwoFollowersOfOneLeader_SettleOnDifferentSpots()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var leader = Npc(1, 0, 0);
			var a = Npc(2, 20, 0);
			var b = Npc(3, 0, 20);
			a.FollowTargetHandle = leader.Handle;
			b.FollowTargetHandle = leader.Handle;
			zone.Add(leader);
			zone.Add(a);
			zone.Add(b);

			for (var i = 0; i < 120; i++)
				world.Tick(0.5);

			Assert.True(Vector3.Distance(a.Position, b.Position) > 0.5f, "two followers should not settle on the same spot");
			Assert.True(Vector3.Distance(a.Position, leader.Position) > 0.5f, "a follower holds a slot beside its leader, not inside it");
		}

		// A DEAD leader drops the binding - a formation has nothing to hold onto once its leader is a corpse. (A
		// player leader is exempt: a dead player is still the escort's leader, which is #262's behaviour.)
		[Fact]
		public void ADeadNpcLeader_ReleasesItsFollowers()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var leader = Npc(1, 0, 0);
			var follower = Npc(2, 6, 0);
			follower.FollowTargetHandle = leader.Handle;
			zone.Add(leader);
			zone.Add(follower);

			// Through MarkDead, like a real kill: setting the vitals alone leaves the respawn countdown at zero, so
			// the very same tick revives the leader before the follower is looked at.
			leader.Vitals!.Health = 0;
			leader.MarkDead();
			world.Tick(0.5);

			Assert.Null(follower.FollowTargetHandle);
		}

		// ...and so does a leader that has left the zone entirely, which is the pre-existing rule and must survive
		// the NPC-leader change.
		[Fact]
		public void AMissingLeader_ReleasesItsFollowers()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var follower = Npc(2, 6, 0);
			follower.FollowTargetHandle = 999;
			zone.Add(follower);

			world.Tick(0.5);

			Assert.Null(follower.FollowTargetHandle);
		}

		private static WorldEntity Npc(uint handle, float x, float z)
		{
			var e = WorldEntity.ForNpc(handle, 4010, new Vector3(x, 0, z), Quaternion.Identity, $"N{handle}", EntityVitals.FromMax(100, 0, 100));
			e.Hostile = false; // a formation is ambient; aggro would take the tick instead
			return e;
		}
	}
}
