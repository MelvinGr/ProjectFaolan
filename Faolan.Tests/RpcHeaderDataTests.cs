using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The RPC header's trailing `headerData` is ONE grammar, not four per-opcode cases: a protobuf varint of the
	// method's registration constant >> 7, omitted when that is zero (Gitea #319/#320).
	//
	// For every interface except PlayerAgent/PlayerInterface the constant IS the method index, so the field is absent
	// for methods 0-127 and exactly `0x01` for methods 128-255. Measured over the corpus: 402,111 of 402,113
	// index-registered packets match, the two misses being mis-framed GameInterface packets at opcodes outside its
	// 16-method range.
	public class RpcHeaderDataTests
	{
		// The boundary is what makes this a rule rather than a coincidence: 0x2080 is index 128 exactly, and it is the
		// lowest opcode in the whole corpus that carries the byte.
		[Theory]
		[InlineData(0x2000, null)]
		[InlineData(0x207F, null)]
		[InlineData(0x2080, 0x01)]
		[InlineData(0x2085, 0x01)] // the ping - 5,917/5,917 captured packets carry it
		[InlineData(0x2082, 0x01)] // the pong - 5,689/5,689
		[InlineData(0x20FF, 0x01)]
		public void HeaderData_IsTheMethodIndexHighBits(int opcode, int? expected)
		{
			var actual = RpcHeaderData.ForOpcode((ushort)opcode);
			if (expected is null)
				Assert.Null(actual);
			else
				Assert.Equal(new[] { (byte)expected.Value }, actual);
		}

		// The two sites that used to hard-code the byte now derive it, and must still emit the captured bytes.
		[Fact]
		public void TheHardCodedSites_DeriveTheSameByte()
		{
			Assert.Equal(new byte[] { 0x01 }, RpcHeaderData.ForOpcode(GameCharInterfaceOpcodes.DispatchPong));
			Assert.Equal(new byte[] { 0x01 }, RpcHeaderData.ForOpcode(CSPlayerInterfaceOpcodes.DispatchPvPMatchMakingInfoRequestReturn));
		}

		// A method BELOW the boundary carries nothing - which is the half of the rule a "constant marker" reading
		// could never account for, and the reason most packets have no header data at all.
		[Fact]
		public void LowIndexMethods_CarryNothing()
		{
			Assert.Null(RpcHeaderData.ForOpcode(GameCharInterfaceOpcodes.DispatchUpdateAggroStatus));
			Assert.Null(RpcHeaderData.ForOpcode((ushort)0x2001));
		}
	}
}
