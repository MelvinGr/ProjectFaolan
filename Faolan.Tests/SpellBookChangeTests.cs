using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the DispatchLearnSpell (s2c 0x200A) / DispatchForgetSpell (0x200B) body - one spell
	// entering or leaving the spellbook. Oracle: the captured grants on the GameCharInterface
	// conversation, whose body is a single big-endian u32 spell id (invariant across all 68 packets; see
	// tools/capture/decode_learn_spell.py). The forget half shares the body - the client's two dispatch readers are
	// the same function twice, see the codec's remark - so the same golden covers both methods.
	public class SpellBookChangeTests
	{
		[Theory, InlineData(0x003006ffu, "003006ff"), InlineData(0x00119110u, "00119110"), InlineData(0x0041b9aeu, "0041b9ae")]
		// TestAccount hoxxling-login-gameplay frame 665
		// hoxxling guild-gameplay frame 3336
		public void BuildSpellBookChangeBody_IsTheBareSpellId(uint spellId, string expectedHex)
		{
			var body = SpellBookChangeCodec.Build(spellId);

			Assert.Equal(expectedHex, Convert.ToHexStringLower(body));
			Assert.Equal(4, body.Length);
			Assert.Equal(spellId, SpellBookChangeCodec.Parse(body).SpellId);
		}
	}
}
