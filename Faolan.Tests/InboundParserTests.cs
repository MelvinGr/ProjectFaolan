using Faolan.Core;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Direct coverage for the inbound-parse codecs extracted from the listener dispatch (the move/drop framing and the
	// Knubot answer index). These are c2s parsers, so a round-trip (build the framing -> parse) pins them.
	public class InboundParserTests
	{
		[Fact]
		public void InventoryMove_RoundTrips_FramingAndDescriptor()
		{
			// The shared [u32 0xC350][u32 charInstance][u32 reserved][protobuf ProtoMoveInventoryItemIIR] framing that
			// MoveInventoryItemIIR + DeleteItemIIR both carry.
			var move = new ProtoMoveInventoryItemIIR
			{
				SourceContainerId = new ProtoID32 { Type = 104, Instance = 0x1234 },
				SourceItemPos = 5,
				DestinationContainerId = new ProtoID32 { Type = 105, Instance = 0x1234 },
				DestinationItemPos = 9
			};
			var body = new FaolanStream().WriteUInt32(0x0000C350u).WriteUInt32(0xABCDu).WriteUInt32(0u).WriteProtobufByteLength(move).ToArray();

			var (charInstance, parsed) = InventoryMoveCodec.Parse(new FaolanStream(body));

			Assert.Equal(0xABCDu, charInstance);
			Assert.Equal(104u, parsed.SourceContainerId.Type);
			Assert.Equal(5, parsed.SourceItemPos);
			Assert.Equal(105u, parsed.DestinationContainerId.Type);
			Assert.Equal(9, parsed.DestinationItemPos);
		}

		[Fact]
		public void KnubotAnswerIndex_IsLastU32BigEndian()
		{
			// The chosen dialogue option is the last u32 (big-endian) of the answer body.
			var body = new FaolanStream().WriteUInt32(0x0000C350u).WriteUInt32(0x11u).WriteUInt32(3u).ToArray();
			Assert.Equal(3u, KnubotCodec.ParseAnswerIndex(body));
		}

		[Fact]
		public void KnubotAnswerIndex_TooShort_IsZero()
		{
			Assert.Equal(0u, KnubotCodec.ParseAnswerIndex([0x01, 0x02])); // < 4 bytes -> no selection
			Assert.Equal(0u, KnubotCodec.ParseAnswerIndex([]));
		}
	}
}
