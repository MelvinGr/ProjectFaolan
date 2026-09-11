using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The record that makes a peaceful NPC's turn real to the client: when a conversation turns its creature on
	// the player, retail restates the creature's collision group to the mob one before anything else.
	//
	// Why this gate exists rather than a comment: everything else in the flip was already implemented and correct -
	// the server model went hostile, the hate list arrived addressed to the player, the client's own re-poll was
	// answered with cCategory 1 - and Saddur still swung at the player for 8 damage while drawing a peaceful nameplate
	// and refusing to be hit. The one record we were not sending is this one, and it is invisible in a diff of what we
	// do send.
	//
	// The corpus makes it exact. Over all 134 captures, every character `CarsGroup` record carrying the mob group is
	// one of the three captured Saddur fights - three captures, three handles, no fourth occurrence anywhere - and in
	// all three the order is the same: `SendScryEvent "Fight"`, this record, the hate list, the client's re-poll. The
	// other 92 character `CarsGroup` records in the corpus carry 0 or 2 and belong to other transitions.
	public class NpcTurnsHostileTests
	{
		// The hex is each frame's body with the carrier's u32 length prefix stripped. Same record, three captures:
		//
		//   Hoxxling_mage-heraldofxotli_login_gameplay                    frame 11363, handle 0x00017E09
		//   xxx_mage-demo_login_play_beach-to-enter-tortage_city          frame  7404, handle 0x0005D763
		//   xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city  frame  8476, handle 0x0005E96F
		[Theory]
		[InlineData(0x00017E09u, "f98e10b30000c35000017e09000000041a00000003")]
		[InlineData(0x0005D763u, "f98e10b30000c3500005d763000000041a00000003")]
		[InlineData(0x0005E96Fu, "f98e10b30000c3500005e96f000000041a00000003")]
		public void TurningAnNpcHostile_RestatesItsCollisionGroup_ByteExact(uint handle, string expected)
		{
			// A character record carrying the world-object env byte (0x00, not the 0x01 characters normally take).
			// That is retail's, not a slip: it is the same "restate the dynel layer copy first" shape SkillPointTests
			// pins on Runspeed, and `CarsGroup` lives on the collision layer the way Runspeed lives on the movement one.
			var body = StatCodec.Build(StatIirs.ForCharacter(handle, (uint)CharacterStatId.CarsGroup, NpcStatCore.MobCarsGroup, StatIirs.WorldObjectEnv));

			Assert.Equal(expected, Convert.ToHexStringLower(body));
		}

		// The value is shared with the last-resort disposition tier, which reads the same group as "this
		// creature fights". Retail treating it as live state is independent evidence for that reading, so the two must
		// not drift apart into two literal 3s.
		[Fact]
		public void TheMobCollisionGroup_IsTheOneTheDispositionTierKeysOn()
		{
			Assert.Equal(3u, NpcStatCore.MobCarsGroup);
		}
	}
}
