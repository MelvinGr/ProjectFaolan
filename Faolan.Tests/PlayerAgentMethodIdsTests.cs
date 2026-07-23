using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// Locks the #320 refactor: the PlayerAgent/PlayerInterface 32-bit method id lives once in PlayerAgentMethodIds and
	// the PlayerRpc helper appends it as the Rpc HeaderData, replacing hand-rolled 4-byte arrays at the send sites.
	// These assert the ids are IDENTICAL to the old literals (so the wire is unchanged) and that the two
	// retail-confirmed special cases carry no standard id.
	public class PlayerAgentMethodIdsTests
	{
		[Theory, InlineData(PlayerInterfaceOpcodes.InitAuth, new byte[] { 0x93, 0x86, 0xee, 0x05 }), InlineData(PlayerInterfaceOpcodes.SendCharacterRealmData, new byte[] { 0xcb, 0xc6, 0xfc, 0x04 }),
		 InlineData(PlayerInterfaceOpcodes.Ox209C, new byte[] { 0x99, 0x95, 0x92, 0x05 }), InlineData(PlayerInterfaceOpcodes.SendCharacterList, new byte[] { 0x8b, 0xd3, 0xa0, 0x0c }),
		 InlineData(PlayerInterfaceOpcodes.SendRealmList, new byte[] { 0xe2, 0xe6, 0xc4, 0x0f }), InlineData(PlayerInterfaceOpcodes.Ox20B9, new byte[] { 0x8b, 0xd8, 0x99, 0x02 })]
		// The exact literals that were hand-rolled at the send sites before the refactor.
		public void HeaderId_MatchesTheOldHandRolledArray(PlayerInterfaceOpcodes opcode, byte[] expected)
		{
			Assert.Equal(expected, PlayerAgentMethodIds.HeaderId((ushort)opcode));
		}

		[Fact]
		public void TheTwoSpecialCases_CarryNoStandardId()
		{
			// SmallCharList sends a 3-byte 0x94A760; OverrideGameTweaks rides the ServiceConnection with a session header-extra.
			Assert.Null(PlayerAgentMethodIds.HeaderId((ushort)PlayerInterfaceOpcodes.SmallCharList));
			Assert.Null(PlayerAgentMethodIds.HeaderId((ushort)PlayerInterfaceOpcodes.OverrideGameTweaks));
		}
	}
}
