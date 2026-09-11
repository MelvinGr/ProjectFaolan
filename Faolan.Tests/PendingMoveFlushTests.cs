using System.Numerics;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The movement a creature walked but never had broadcast, taken when its move ends outside the tick.
	//
	// The bug this exists for: the world tick only emits a MoveTask once the entity has covered `MoveBroadcastSegment`
	// (6u) since the last one, and flushes the remainder in its own `EndMove` on arrival. Death ends a move without
	// going through that - so a creature killed mid-chase carried up to 6u the client had never been told about, and
	// the death pose states the true position. On screen the corpse jumped that far onto its killer and only then
	// played its death animation. Killing one in melee never showed it, because the chase had already ended and
	// flushed - which is exactly why it reads as "only when I kill them from a distance".
	//
	// Retail keeps the two in agreement rather than posing elsewhere: over the whole corpus **250 of 267** death
	// poses sit at distance 0.00 from the end of that handle's preceding MoveTask (median 0.00). So the pose is right
	// and the client was simply behind - flush, don't move the pose.
	public class PendingMoveFlushTests
	{
		private static WorldEntity Npc(Vector3 at)
		{
			return WorldEntity.ForNpc(0x40, 4010, at, Quaternion.Identity, "Scavenger Lackey");
		}

		[Fact]
		public void AMoveKilledMidSegment_HandsBackTheUnbroadcastPart()
		{
			var npc = Npc(new Vector3(104, 10, 100)); // where the server has walked it to
			npc.MoveOrigin = new Vector3(100, 10, 100); // ...and where the client last saw it
			npc.Moving = true;

			Assert.True(npc.TakePendingMoveSegment());

			// The segment the broadcaster sends is exactly the gap the client is missing.
			Assert.Equal(new Vector3(100, 10, 100), npc.MoveFrom);
			Assert.Equal(new Vector3(104, 10, 100), npc.Position);

			// ...and the anchor re-advances, so a second flush cannot re-send the same ground.
			Assert.Equal(new Vector3(104, 10, 100), npc.MoveOrigin);
			Assert.False(npc.Moving);
			Assert.False(npc.TakePendingMoveSegment());
		}

		// The melee case - the chase already ended and flushed, so the entity is standing on its anchor. Sending a
		// degenerate from==to segment here would be a packet retail does not send, so it must report nothing pending.
		[Fact]
		public void AnEntityStandingOnItsAnchor_HasNothingToFlush()
		{
			var npc = Npc(new Vector3(100, 10, 100));
			npc.MoveOrigin = new Vector3(100, 10, 100);
			npc.Moving = true;

			Assert.False(npc.TakePendingMoveSegment());
			Assert.False(npc.Moving); // still ends the move
		}

		// An entity that was never moving at all - the overwhelming majority of kills, since a mob that never aggroed
		// is standing where it spawned.
		[Fact]
		public void AnEntityThatWasNotMoving_HasNothingToFlush()
		{
			var npc = Npc(new Vector3(140, 10, 100));
			npc.MoveOrigin = new Vector3(100, 10, 100); // stale anchor from an older move
			npc.Moving = false;

			Assert.False(npc.TakePendingMoveSegment());
			Assert.Equal(new Vector3(100, 10, 100), npc.MoveOrigin); // untouched - no phantom 40u segment
		}
	}
}
