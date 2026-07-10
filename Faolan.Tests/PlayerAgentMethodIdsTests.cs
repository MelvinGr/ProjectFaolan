using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Xunit;

namespace Faolan.Tests
{
	// Locks the #320 refactor: the PlayerAgent/PlayerInterface 32-bit method id now lives once in
	// PlayerAgentMethodIds and WriteHeader appends it, replacing hand-rolled 4-byte arrays at the send sites. These
	// assert the emitted bytes are IDENTICAL to the old literals (so the wire is unchanged) and that the two
	// retail-confirmed special cases carry no standard id.
	public class PlayerAgentMethodIdsTests
	{
		private static readonly byte[] Sender = [0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02];
		private static readonly byte[] Receiver = [0x0d, 0x16, 0x91, 0x35, 0x1d, 0x10, 0x08];

		[Theory]
		// The exact literals that were hand-rolled at the send sites before the refactor.
		[InlineData(PlayerAgentResponseOpcodes.InitAuth, new byte[] { 0x93, 0x86, 0xee, 0x05 })]
		[InlineData(PlayerAgentResponseOpcodes.SendCharacterRealmData, new byte[] { 0xcb, 0xc6, 0xfc, 0x04 })]
		[InlineData(PlayerAgentResponseOpcodes.Ox209C, new byte[] { 0x99, 0x95, 0x92, 0x05 })]
		[InlineData(PlayerAgentResponseOpcodes.SendCharacterList, new byte[] { 0x8b, 0xd3, 0xa0, 0x0c })]
		[InlineData(PlayerAgentResponseOpcodes.SendRealmList, new byte[] { 0xe2, 0xe6, 0xc4, 0x0f })]
		[InlineData(PlayerAgentResponseOpcodes.Ox20B9, new byte[] { 0x8b, 0xd8, 0x99, 0x02 })]
		public void HeaderId_MatchesTheOldHandRolledArray(PlayerAgentResponseOpcodes opcode, byte[] expected)
		{
			Assert.Equal(expected, PlayerAgentMethodIds.HeaderId((ushort)opcode));
		}

		[Fact]
		public void WriteHeader_AutoIdOverload_IsByteIdenticalToTheExplicitArray()
		{
			var auto = new PacketStream().WriteHeader(Sender, Receiver, PlayerAgentResponseOpcodes.InitAuth).ToArray();
			var explicitArray = new PacketStream().WriteHeader(Sender, Receiver, PlayerAgentResponseOpcodes.InitAuth, [0x93, 0x86, 0xee, 0x05]).ToArray();

			Assert.Equal(explicitArray, auto);
		}

		[Fact]
		public void TheTwoSpecialCases_CarryNoStandardId()
		{
			// SmallCharList sends a 3-byte 0x94A760; Ox1A07 rides the ServiceConnection with a session header-extra.
			Assert.Null(PlayerAgentMethodIds.HeaderId((ushort)PlayerAgentResponseOpcodes.SmallCharList));
			Assert.Null(PlayerAgentMethodIds.HeaderId((ushort)PlayerAgentResponseOpcodes.Ox1A07));
		}
	}
}
