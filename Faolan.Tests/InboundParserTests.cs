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
			// MoveInventoryItemIIR: [u32 0xC350][u32 charInstance][u32 reserved][protobuf ProtoMoveInventoryItemIIR].
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

		// DeleteItemIIR carries ProtoDeleteItemIIR { container_id, item_pos, template_id } - NOT the sibling move
		// message. Proven from the client: DeleteItemIIR_t's constructor installs ProtoDeleteItemIIR::vftable and
		// fills three fields, while MoveInventoryItemIIR_t installs ProtoMoveInventoryItemIIR::vftable and fills a
		// fourth (tools/ghidra/DumpDeleteItemFns.java).
		[Fact]
		public void InventoryDelete_RoundTrips_FramingAndDescriptor()
		{
			var drop = new ProtoDeleteItemIIR
			{
				ContainerId = new ProtoID32 { Type = 104, Instance = 0x1234 },
				ItemPos = 7,
				TemplateId = new ProtoID32 { Type = 3774542, Instance = 0 }
			};
			var body = new FaolanStream().WriteUInt32(0x0000C350u).WriteUInt32(0xABCDu).WriteUInt32(0u).WriteProtobufByteLength(drop).ToArray();

			var (charInstance, parsed) = InventoryDeleteCodec.Parse(new FaolanStream(body));

			Assert.Equal(0xABCDu, charInstance);
			Assert.Equal(104u, parsed.ContainerId.Type);
			Assert.Equal(7, parsed.ItemPos);
			Assert.Equal(3774542u, parsed.TemplateId.Type);
		}

		// The regression guard that the old synthetic round-trip could not be: the two messages ALIAS on the wire
		// for fields 1-3, so a delete decodes into the move message with NO error and files template_id into
		// DestinationContainerId. That is exactly how parsing drops as moves went unnoticed. Pin the aliasing so
		// nobody "simplifies" the two codecs back into one.
		[Fact]
		public void DeleteItem_DecodesIntoTheMoveMessage_WhichIsWhyTheBugHid()
		{
			var drop = new ProtoDeleteItemIIR
			{
				ContainerId = new ProtoID32 { Type = 104, Instance = 0x1234 },
				ItemPos = 7,
				TemplateId = new ProtoID32 { Type = 3774542, Instance = 0 }
			};
			var wire = new FaolanStream().WriteProtobufByteLength(drop).ToArray();

			// Same bytes, read as the WRONG message: no throw, fields 1-2 land correctly...
			var asMove = new FaolanStream(wire).ReadProtobufByteLength<ProtoMoveInventoryItemIIR>();
			Assert.Equal(104u, asMove.SourceContainerId.Type);
			Assert.Equal(7, asMove.SourceItemPos);

			// ...and the item TEMPLATE id silently poses as the destination container.
			Assert.Equal(3774542u, asMove.DestinationContainerId.Type);
			Assert.Equal(0, asMove.DestinationItemPos); // field 4 absent - the only structural tell
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
