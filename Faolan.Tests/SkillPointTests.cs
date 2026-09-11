using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// Skillpoint spends, against the four annotated spends in
	// NecroL80_mage-necromancer_login_gameplay_annotated_3. The hex is the frame body with the carrier's u32 length
	// prefix stripped; the character is 0x0802E5D4 throughout, and the annotations name the skill each time:
	//
	//   17:44:45  "just put 1 skillpoint in run speed"    17:45:18  "just put 1 skilpoint in hiding"
	//   17:45:05  "just put 1 skilpoint in climbing"      17:45:30  "just put 1 skilpoint in perception"
	public class SkillPointTests
	{
		private const uint Character = 0x0802E5D4;

		// The 0x3F1 trap, caught in the act. These bodies were first split as
		//   [u32 0x0000000B][u8 0xD3][pairs]
		// - two constants that are really one misaligned word. The correct split puts the base record's `env` byte
		// where the leading zero-run seems to continue, leaving 0x00000BD3 = 3 * 0x3F1 = a two-element list. Both
		// readings consume the same 37 bytes and both "work" on all four captures, which is exactly why the framing
		// word has to be checked against ListFrame rather than eyeballed.
		[Fact]
		public void CapturedSpendBodies_AreAListFrameNotTwoConstants()
		{
			Assert.Equal(2, ListFrame.CountOf(0x0BD3));
			Assert.Equal(0x0BD3u, ListFrame.ForCount(2));
		}

		[Theory]
		[InlineData("00000021d558ff160000c3500802e5d40000000bd3000000f3ffffffff0000043d00000001", CharacterStatId.RunSpeed)] // frame 953
		[InlineData("00000021d558ff160000c3500802e5d40000000bd3000000f3ffffffff0000033200000001", CharacterStatId.Climbing)] // frame 1040
		[InlineData("00000021d558ff160000c3500802e5d40000000bd3000000f3ffffffff0000033c00000001", CharacterStatId.Hiding)] // frame 1121
		[InlineData("00000021d558ff160000c3500802e5d40000000bd3000000f3ffffffff0000033e00000001", CharacterStatId.Perception)] // frame 1179
		public void ParseCapturedSpend_IsAConservingTransferOutOfThePool(string frameHex, CharacterStatId skill)
		{
			// The frame carries the carrier's u32 length ahead of the record; the codec reads the record.
			var body = Convert.FromHexString(frameHex).AsSpan(sizeof(uint));

			var spend = SkillCodec.Parse(body);

			Assert.Equal(EntityClass.Character, spend.EntityClass);
			Assert.Equal(Character, spend.Handle);
			Assert.Equal(0x00, spend.Env);
			Assert.Equal([((uint)CharacterStatId.SkillPoints, -1), ((uint)skill, 1)], spend.Adjustments.Select(a => (a.StatId, a.Delta)));

			// The deltas cancel: a spend moves points, it does not create them.
			Assert.Equal(0, spend.Adjustments.Sum(a => a.Delta));

			// ...and re-Builds byte-exact.
			Assert.Equal(Convert.ToHexStringLower(body), Convert.ToHexStringLower(SkillCodec.Build(spend)));
		}

		// The reply is two ordinary StatIIR records - the skill at its new total, then the pool - so the spend needs
		// no bespoke answer message. The captured totals are the necromancer's own sheet: Climbing 300 -> 301,
		// Hiding 250 -> 251, Perception 560 -> 561, and the pool 1892 counting down one per spend.
		[Theory]
		[InlineData(CharacterStatId.Climbing, 301u, 1890u, "f98e10b30000c3500802e5d401000003320000012d", "f98e10b30000c3500802e5d401000000f300000762")] // frame 1041
		[InlineData(CharacterStatId.Hiding, 251u, 1889u, "f98e10b30000c3500802e5d4010000033c000000fb", "f98e10b30000c3500802e5d401000000f300000761")] // frame 1122
		[InlineData(CharacterStatId.Perception, 561u, 1888u, "f98e10b30000c3500802e5d4010000033e00000231", "f98e10b30000c3500802e5d401000000f300000760")] // frame 1180
		public void BuildSpendReply_MatchesTheCapturedStatPair(CharacterStatId skill, uint total, uint pool, string skillHex, string poolHex)
		{
			Assert.Equal(skillHex, Convert.ToHexStringLower(StatCodec.Build(StatIirs.ForCharacter(Character, (uint)skill, total))));
			Assert.Equal(poolHex, Convert.ToHexStringLower(StatCodec.Build(StatIirs.ForCharacter(Character, (uint)CharacterStatId.SkillPoints, pool))));
		}

		// Runspeed - and only Runspeed - is restated with the base byte clear before the ordinary record. Sweeping
		// every captured character StatIIR shows env=0 is a property of the stat (22 ids of 53 carry one, Runspeed
		// among them; the other three skills never do), not of "the first point in a skill".
		[Fact]
		public void BuildRunspeedSpendReply_LeadsWithTheDynelLayerCopy()
		{
			Assert.Equal("f98e10b30000c3500802e5d4000000043d00000001",
				Convert.ToHexStringLower(StatCodec.Build(StatIirs.ForCharacter(Character, (uint)CharacterStatId.RunSpeed, 1, StatIirs.WorldObjectEnv))));

			Assert.Equal("f98e10b30000c3500802e5d4010000043d00000001",
				Convert.ToHexStringLower(StatCodec.Build(StatIirs.ForCharacter(Character, (uint)CharacterStatId.RunSpeed, 1))));
		}
	}
}
