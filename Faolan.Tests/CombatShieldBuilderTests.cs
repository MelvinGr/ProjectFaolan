using System;
using Xunit;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Tests
{
	// Byte-exact gate for the CombatShieldIIR (0xCFFD0519) guard broadcast (#20 decode 2). The golden is a
	// captured instance (entity 0x0000AA72, value 0 - every captured instance carries 0: the Tortage mobs never
	// raise guard, which is why the distribution semantics had to come from the client RE instead; see
	// CombatShieldBuilder).
	public class CombatShieldBuilderTests
	{
		[Fact]
		public void Build_IsByteIdenticalToCapturedInstance()
		{
			var captured = Convert.FromHexString("cffd05190000c3500000aa7200000000");

			var built = CombatShieldBuilder.Build(0x0000AA72, 0);

			Assert.Equal(captured, built);
		}

		[Fact]
		public void Build_PacksDirectionsAsDecimalDigits()
		{
			// hundreds = Left, tens = Mid, units = Right (client Activate FUN_0113de90).
			var built = CombatShieldBuilder.Build(0x1234, 2, 1, 0);

			Assert.Equal(210u, (uint)((built[12] << 24) | (built[13] << 16) | (built[14] << 8) | built[15]));
		}

		[Fact]
		public void DefaultDistribution_IsTheClientClampValue()
		{
			// The client's own overflow clamp resets stat 0xA5 to 111 - one shield per arc.
			Assert.Equal(111u, CombatShieldBuilder.DefaultDistribution);
		}
	}
}
