using Faolan.Core.Network.Codecs.GameServer;
using System;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The NormalAttack BccAck echoes the request args with field3 (the behaviour "phase", tag 0x18) inserted after
	// field2 - InsertBehaviorPhaseField. Locked against the strickenbla combat capture: its swing-1 request args,
	// with phase 3 spliced in, equal the server's BccAck args byte-for-byte.
	public class BehaviorPhaseFieldTests
	{
		private static readonly byte[] AckArgs = Convert.FromHexString("08ecfd0210011803280a3000520708d0860310d75858486034");

		// Capture ground truth (account-stricken998 strickenbla combat, swing 1).
		private static readonly byte[] RequestArgs = Convert.FromHexString("08ecfd021001280a3000520708d0860310d75858486034");

		[Fact]
		public void InsertBehaviorPhaseField_AfterField2_MatchesCapturedAck()
		{
			var spliced = BehaviorAckBody.InsertBehaviorPhaseField(RequestArgs, 3);

			Assert.Equal(AckArgs, spliced); // field3=3 inserted right after field2, rest unchanged
		}

		[Fact]
		public void InsertBehaviorPhaseField_AppendsWhenNoField2()
		{
			var args = Convert.FromHexString("0801"); // only field1
			var spliced = BehaviorAckBody.InsertBehaviorPhaseField(args, 5);

			Assert.Equal("080118 05".Replace(" ", ""), Convert.ToHexString(spliced).ToLowerInvariant());
		}

		[Fact]
		public void InsertBehaviorPhaseField_EncodesMultiByteVarintPhase()
		{
			// args = field1(08 01) field2(10 01) field5(28 0a); phase 200 -> varint c8 01, inserted as 18 c8 01
			// right after field2.
			var spliced = BehaviorAckBody.InsertBehaviorPhaseField(Convert.FromHexString("0801100128 0a".Replace(" ", "")), 200);

			Assert.Equal("08011001 18c801 280a".Replace(" ", ""), Convert.ToHexString(spliced).ToLowerInvariant());
		}
	}
}
