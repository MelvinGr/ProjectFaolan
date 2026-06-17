using System;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the DispatchLearnSpell (s2c 0x200A) body - the live spell-grant packet (#73). Oracle:
	// the captured grants on the GameCharInterface conversation, whose body is a single big-endian u32 spell id
	// (invariant across all 68 packets; see Captures/scripts/decode_learn_spell.py).
	public class LearnSpellTests
	{
		[Theory]
		[InlineData(0x003006ffu, "003006ff")] // faolan hoxxling-login-gameplay frame 665
		[InlineData(0x00119110u, "00119110")] // hoxxling guild-gameplay frame 3336
		[InlineData(0x0041b9aeu, "0041b9ae")]
		public void BuildLearnSpellBody_IsTheBareSpellId(uint spellId, string expectedHex)
		{
			var body = GameServerListener.BuildLearnSpellBody(spellId);

			Assert.Equal(expectedHex, Convert.ToHexStringLower(body));
			Assert.Equal(4, body.Length);
		}
	}
}
