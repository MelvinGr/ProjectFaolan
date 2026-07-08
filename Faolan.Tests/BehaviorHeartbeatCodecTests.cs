using System;
using Xunit;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Tests
{
	// Byte-exact construct of the short-form BehaviorControlCenterIIR idle/AI heartbeat (#225) vs the captured pulses
	// in NecroL80_..._leave_king_conan_castle-Dismount.pcapng (frame 105). Actor + monotonic tick are the only variables.
	public class BehaviorHeartbeatCodecTests
	{
		[Fact]
		public void Build_MultiByteTick_WidensPayloadLen()
		{
			// tick 300 (> 127) → a 2-byte varint (ac 02) → payload len 0x0b, matching the ticket's frame-86 sample shape.
			var bytes = BehaviorHeartbeatCodec.Build(0x00008322, 300);
			Assert.Equal("642cd3d60000c350000083220004000000070000000b08c5c30218ac0228183000", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void Build_Tick1_MatchesCapture()
		{
			// frame 105: actor 0x00008322, tick 1 (payload len 0x0a).
			var bytes = BehaviorHeartbeatCodec.Build(0x00008322, 1);
			Assert.Equal("642cd3d60000c350000083220004000000070000000a08c5c302180128183000", Convert.ToHexStringLower(bytes));
		}
	}
}
