using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Xunit;

namespace Faolan.Tests
{
	// The RPC header's trailing `headerData` is ONE grammar, not four per-opcode cases: a protobuf varint of the
	// method's registration constant >> 7, omitted when that is zero (Gitea #319/#320).
	//
	// For every interface except PlayerAgent/PlayerInterface the constant IS the method index, so the field is absent
	// for methods 0-127 and exactly `0x01` for methods 128-255. Measured over the corpus: 404,208 of 404,210 frames
	// match. The two misses are not packets - they sit inside a desynced decompressed region of one capture and fail an
	// independent CRC recompute under every alignment (#609).
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

		// What the code splits into `[u16 opcode][headerData]` is ONE protobuf field: tag 0x20 (field 4, varint) whose
		// value is the method's registration constant, whole. The split is byte-identical - the "opcode" is the tag
		// plus the varint's first group, and the "headerData" is the rest - and this is the gate that says so, method
		// by method, against the client's own registration tables. It is also why `0x2080` is the boundary and why the
		// trailing byte is always exactly `0x01`: those are properties of a varint's first group, not of a marker.
		[Fact]
		public void TheOpcodeIsTheMethodVarint()
		{
			foreach (var (opcode, wireId) in PlayerAgentMethodIds.ById)
			{
				// The observed opcodes are all on the hashed pair, so their constants need more than one group.
				var constant = (wireId << 7) | (uint)(opcode & 0x7F);
				var varint = PlayerAgentMethodIds.Varint(constant);

				Assert.Equal(0x20, opcode >> 8);
				Assert.Equal(varint[0], (byte)(opcode & 0xFF));
				Assert.Equal(varint[1..], RpcHeaderData.For(Endpoints.PlayerInterfaceType, opcode) ?? RpcHeaderData.For(Endpoints.PlayerAgentType, opcode));
			}

			// And on an index-registered interface the same reassembly gives the plain method index.
			Assert.Equal(133u, Reassemble(0x2085, RpcHeaderData.For(Endpoints.GameCharAgentType, 0x2085)!));
			Assert.Equal(9u, Reassemble(0x2009, []));
			return;

			static uint Reassemble(ushort opcode, byte[] tail)
			{
				var value = (uint)(opcode & 0x7F);
				for (var i = 0; i < tail.Length; i++)
					value |= (uint)(tail[i] & 0x7F) << (7 * (i + 1));
				return value;
			}
		}

		// The collision #608 is about, stated as bytes: the SAME opcode with two different tails is two different
		// methods, and only the reassembled varint tells them apart. Retail sends all four of these; an opcode-keyed
		// dispatch would read an 8-byte `AuthenticateLoggedIn` as the no-arg `ReceivedCharacterSettings` ack.
		[Theory, InlineData(0x20EC, "a4f2e303", PlayerAgentMethods.ReceivedCharacterSettings), InlineData(0x20EC, "bce4ec09", PlayerAgentMethods.AuthenticateLoggedIn),
		 InlineData(0x20D6, "dd9ec209", PlayerAgentMethods.VerifyLanguageSetting), InlineData(0x20D6, "ac98eb04", PlayerAgentMethods.UpdateCharacterPreview)]
		public void OneOpcode_CanNameTwoMethods(int opcode, string tail, PlayerAgentMethods expected)
		{
			Assert.Equal(expected, (PlayerAgentMethods)RpcHeaderData.MethodConstant((ushort)opcode, Convert.FromHexString(tail)));
			Assert.Equal((ushort)opcode, (ushort)(0x2080 | ((uint)expected & 0x7F)));
		}

		// The receiver CLASS picks the registration scheme, which is the whole reason `For` takes it: the same opcode
		// number means a whole method index on 15 of the 17 classes and only the first varint group on the other two. Rows
		// are one per class, taken from the corpus scan of all 120 dumps grouped by (ReceiverClass, Opcode).
		[Theory, InlineData(Endpoints.GameCharInterfaceType, 0x2082, "01"), InlineData(Endpoints.GameCharInterfaceType, 0x206B, null), InlineData(Endpoints.CSPlayerInterfaceType, 0x20A5, "01"),
		 InlineData(Endpoints.GameInterfaceType, 0x2000, null), InlineData(Endpoints.UniverseAgentType, 0x2001, null),
		 // The two hash-registered classes take a whole method id where the index rule would say a flat 0x01.
		 InlineData(Endpoints.PlayerInterfaceType, 0x208C, "cbc6fc04"), InlineData(Endpoints.PlayerInterfaceType, 0x20A5, "94a760"), InlineData(Endpoints.PlayerAgentType, 0x2083, "96cbe509")]
		public void TheReceiverClass_PicksTheRegistrationScheme(uint receiverType, int opcode, string? expected)
		{
			var actual = RpcHeaderData.For(receiverType, (ushort)opcode);
			Assert.Equal(expected, actual is null ? null : Convert.ToHexString(actual).ToLowerInvariant());
		}

		// The point of #609: a send supplies nothing and still gets the byte retail always includes. Before this, every
		// opcode >= 0x2080 that did not explicitly pass it went out a byte short.
		[Fact]
		public void ASendSuppliesNothing_AndStillCarriesTheByte()
		{
			var pong = new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameCharInterfaceType, 0x3BC035EC), GameCharInterfaceOpcodes.DispatchPong,
				(byte[])[0x00, 0x00, 0x00, 0x07]);

			Assert.Equal(new byte[] { 0x01 }, pong.HeaderData.ToArray());
			Assert.Equal(new byte[] { 0x01 }, Rpc.Parse(pong.Serialize()).HeaderData.ToArray());
		}

		// ...and a method below the boundary stays bare, which is the half the trade replies used to get wrong: all
		// seven ride opcodes 0x2068-0x206E (indices 104-110) and every one of them hand-passed a 0x01 that retail's own
		// captures do not carry.
		[Theory, InlineData(GameCharInterfaceOpcodes.DispatchRequestTrade), InlineData(GameCharInterfaceOpcodes.Reserved0x206B), InlineData(GameCharInterfaceOpcodes.DispatchPartnerUpdatedCash),
		 InlineData(GameCharInterfaceOpcodes.DispatchCharacterNameReply)]
		public void ALowIndexSend_StaysBare(GameCharInterfaceOpcodes opcode)
		{
			Assert.True(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x10), Endpoints.Endpoint(Endpoints.GameCharInterfaceType, 0x3BC035EC), opcode, ReadOnlyMemory<byte>.Empty)
				.HeaderData.IsEmpty);
		}

		// An INBOUND frame keeps the tail it arrived with rather than being re-derived. Two reasons, and both are real:
		// a corrupt or desynced buffer can carry a tail that contradicts the rule, and on PlayerAgent the opcode is only
		// the method varint's first group, so re-deriving would confidently rename the message.
		[Fact]
		public void Parse_KeepsATailThatContradictsTheRule()
		{
			var frame = new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameInterfaceType, 0x3BC035EC), (ushort)0x20A8, ReadOnlyMemory<byte>.Empty)
				{ HeaderDataOverride = ReadOnlyMemory<byte>.Empty }.Serialize();

			// 0x20A8 is index 168, so the rule says 0x01 - and the two captured packets at this opcode carry nothing.
			Assert.Equal(new byte[] { 0x01 }, RpcHeaderData.For(Endpoints.GameInterfaceType, 0x20A8));
			Assert.True(Rpc.Parse(frame).HeaderData.IsEmpty);
			Assert.Equal(frame, Rpc.Parse(frame).Serialize());
		}
	}
}
