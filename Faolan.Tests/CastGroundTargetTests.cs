using System;
using System.Numerics;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The CastSpell (BehaviorControlCenter 0x642CD3D6) trailer carries a ground-target reticle for AoE spells. Decoded
	// from NecroL80_..._actions-described-in-chat (the "Casting Deathly Blizzard (Rank 5) ... (AOE)" cast, frame 1292)
	// vs the entity-targeted Ice Strike cast (Stickienecro, frame 377). Locks CastGroundTarget's parse + detection.
	public class CastGroundTargetTests
	{
		private static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));

		[Fact]
		public void ParsesGroundTargetFromDeathlyBlizzardTrailer()
		{
			// f1292 trailer (bytes after [from][spellId]): [00][X BE][Y BE][Z BE][0][0][0x41A][01].
			var trailer = Convert.FromHexString("00445a88714331dcbf4430a65f00000000000000000000041a01");

			Assert.True(CastGroundTarget.TryParse(trailer, out var pos));
			Assert.Equal(new Vector3(F(0x445A8871), F(0x4331DCBF), F(0x4430A65F)), pos);
		}

		[Fact]
		public void RejectsEntityTargetedCast()
		{
			// f377 Ice Strike trailer: lead byte 01 then a ZEROED Vector3 (the target rides SpellTarget, not the trailer).
			var trailer = Convert.FromHexString("010000000000000000000000c800");

			Assert.False(CastGroundTarget.TryParse(trailer, out var pos));
			Assert.Equal(Vector3.Zero, pos);
		}

		[Fact]
		public void RejectsShortTrailer()
		{
			Assert.False(CastGroundTarget.TryParse([0x00, 0x01, 0x02], out _));
			Assert.False(CastGroundTarget.TryParse([], out _));
			Assert.False(CastGroundTarget.TryParse(null, out _));
		}
	}
}
