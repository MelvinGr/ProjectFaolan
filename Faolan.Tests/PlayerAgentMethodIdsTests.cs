using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The PlayerAgent/PlayerInterface method id lives once in PlayerAgentMethodIds and the PlayerRpc helper appends it
	// as the Rpc HeaderData, replacing hand-rolled byte arrays at the send sites. These assert the emitted bytes are
	// IDENTICAL to the literals that were there before, which is what says the wire is unchanged - the id is DERIVED
	// now (`registrationConstant >> 7`, varint-encoded), so the gate is that deriving it reproduces retail (#320).
	public class PlayerAgentMethodIdsTests
	{
		[Theory, InlineData(PlayerInterfaceOpcodes.AuthenticateAck, new byte[] { 0x93, 0x86, 0xee, 0x05 }), InlineData(PlayerInterfaceOpcodes.PlayerSetupComplete, new byte[] { 0xcb, 0xc6, 0xfc, 0x04 }),
		 InlineData(PlayerInterfaceOpcodes.OnFacebookTokenResult, new byte[] { 0x99, 0x95, 0x92, 0x05 }), InlineData(PlayerInterfaceOpcodes.UpdateClientPlayerData, new byte[] { 0x8b, 0xd3, 0xa0, 0x0c }),
		 InlineData(PlayerInterfaceOpcodes.SetDimensionList, new byte[] { 0xe2, 0xe6, 0xc4, 0x0f }), InlineData(PlayerInterfaceOpcodes.SendCharacterSettings, new byte[] { 0x8b, 0xd8, 0x99, 0x02 })]
		// The exact literals that were hand-rolled at the send sites before the refactor.
		public void HeaderId_MatchesTheOldHandRolledArray(PlayerInterfaceOpcodes opcode, byte[] expected)
		{
			Assert.Equal(expected, PlayerAgentMethodIds.HeaderId((ushort)opcode));
		}

		// The 3-byte tail used to be listed as a "constant 0x94A760" special case, alongside the 1-byte 0x01 marker.
		// It is neither special nor constant: it is this method's id, and it is three bytes because a varint is as long
		// as its value needs. Deriving it is what showed the "four header-data cases" were one grammar (#320).
		[Fact]
		public void TheThreeByteTail_IsJustAShorterVarint()
		{
			Assert.Equal(new byte[] { 0x94, 0xa7, 0x60 }, PlayerAgentMethodIds.HeaderId((ushort)PlayerInterfaceOpcodes.SendCharacterSettingsCheckSumMap));
		}

		// The ONE genuine exception left: OverrideGameTweaks rides the ServiceConnection endpoint and carries a 13-byte
		// protobuf endpoint reference where its id would go, so its call site passes that verbatim.
		[Fact]
		public void OverrideGameTweaks_CarriesNoMethodId()
		{
			Assert.Null(PlayerAgentMethodIds.HeaderId((ushort)PlayerInterfaceOpcodes.OverrideGameTweaks));
		}

		// Every id we emit is `registrationConstant >> 7` — the rule, asserted against the client's own table rather
		// than against a list of observed bytes, so a new method needs no capture.
		[Fact]
		public void EveryRegistrationConstant_YieldsItsWireId()
		{
			Assert.Equal(25, PlayerAgentMethodIds.PlayerAgentRegistration.Count);
			Assert.Equal(26, PlayerAgentMethodIds.PlayerInterfaceRegistration.Count);
			Assert.Equal(0x96CBE509u >> 0, Bytes(PlayerAgentMethodIds.PlayerAgentRegistration["Authenticate"]));
			Assert.Equal(0xA4F2E303u, Bytes(PlayerAgentMethodIds.PlayerAgentRegistration["ReceivedCharacterSettings"]));
			Assert.Equal(0x9386EE05u, Bytes(PlayerAgentMethodIds.PlayerInterfaceRegistration["AuthenticateAck"]));
			return;

			// The varint bytes read back as the big-endian word the captures show, which is how the corpus recorded them.
			static uint Bytes(uint registration)
			{
				var v = PlayerAgentMethodIds.Varint(PlayerAgentMethodIds.WireId(registration));
				uint packed = 0;
				foreach (var b in v)
					packed = (packed << 8) | b;
				return packed;
			}
		}
	}
}
