using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Importer.Extractors
{
	// A captured on-wire packet with the metadata the extractors need, uniform across the .json dumps and the
	// .pcapng analyzer output. Constructed by TsharkCaptureReader; consumed by every extractor and importer.
	//
	// It lived inside ConversationExtractor.cs, which is why `Body` did not: a shared type filed under one of its
	// consumers does not look like somewhere to put shared behaviour. So the framing walk was copy-pasted into six
	// extractors verbatim, three MORE reached across into `WorldObjectExtractor.GetBody` for it, and a seventh copy
	// in CaptureCharacterImporter had already drifted to a stricter guard - the drift the duplication predicts.
	// The trailing four are DUMP METADATA rather than wire content: they describe where a packet sat in the capture,
	// not what it says. Every extractor ignores them and every existing construction site keeps compiling, because
	// they default. They exist for the `--report timeline` reader, which has to say WHEN, on which service tier and
	// in which direction something happened - questions the bytes alone cannot answer.
	public readonly record struct CapturedPacket(
		long Ticks, bool IsDownload, uint Opcode, uint SubOpcode, byte[] Bytes,
		int Frame = 0, string Direction = "", int Port = 0, string Protocol = "Conan",
		uint SenderClass = 0, uint ReceiverClass = 0)
	{
		// Whether this packet is the PassBlob IIR CARRIER - the one whose body is a length-prefixed IIR record rather
		// than a typed RPC argument list. Almost every extractor opens by filtering on it.
		//
		// The value comes from the opcode enum, because it already has a name: `PassBlob` c2s
		// (GameAgentOpcodes) and `DispatchPassBlob` s2c (GameInterfaceOpcodes) are the two directions of ONE method
		// on ONE conversation, and every 0x2000 packet in the corpus is GameAgent (0xB171CE13) <-> GameInterface
		// (0x6C67C147) - all 30158 of them. Our own world sends agree: WorldIir.Send and every outbound builder use
		// GameInterfaceOpcodes.DispatchPassBlob.
		//
		// It is a PROPERTY here rather than a shared constant because the fourteen call sites do not agree on
		// direction - some pin IsDownload, most do not - so naming the direction at each of them would be inventing
		// precision the filter does not have. A bool also works in a property pattern, which a cast expression
		// cannot: `p is { IsIirCarrier: true, SubOpcode: X }`.
		public bool IsIirCarrier => Opcode == (uint)GameAgentOpcodes.PassBlob;

		// The body within the full on-wire packet: [len4][crc4][headerLen4][header][body]. Empty when the packet is
		// too short to carry the fixed 12-byte prologue, or when its header length runs past the end.
		public byte[] Body
		{
			get
			{
				if (Bytes.Length < 12)
					return [];

				var headerLen = (int)Bytes.ReadU32Be(8);
				var bodyStart = 12 + headerLen;
				return bodyStart <= Bytes.Length
					? Bytes[bodyStart..]
					: [];
			}
		}
	}
}
