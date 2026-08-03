using System;
using Faolan.Core.Extensions;
using Faolan.Core.Enums;

namespace Faolan.Importer.Captures
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
		long Ticks, bool IsDownload, uint WireOpcode, uint SubOpcode, byte[] Bytes,
		int Frame = 0, string Direction = "", int Port = 0, string Protocol = "Conan",
		uint SenderClass = 0, uint ReceiverClass = 0)
	{
		// ## The positional `WireOpcode`, and why it is not `Rpc.Method`
		//
		// It is a `Protocol.WireOpcode` - the method field's first two bytes as one u16 - read verbatim out of the
		// dump's `"Opcode"` field by `TsharkCaptureReader`. A RECORDED value, produced by the Wireshark plugin before
		// our code sees it, and it stays in that space rather than being converted once at the reader because the
		// conversion is not total: on the fifteen index-registered classes the low byte IS the method, but on the
		// hash-registered PlayerAgent/PlayerInterface pair it keeps 7 of 32 bits and names two methods at once
		// (#613/#608), so a `Method` here would be a lie on exactly those frames.
		//
		// ⚠️ Since #615 the method enums hold `method_instance` itself (`GameAgentMethods.PassBlob = 0x00`) while this
		// holds the two bytes recorded for it (`0x2000`), so `WireOpcode == (uint)SomeMethods.X` compiles and is
		// silently FALSE - the failure this ticket was executed to make impossible. Hence `Wire(...)`: the one place
		// the two spaces are bridged, and what every extractor's filter goes through instead of a bare cast.
		// Fully qualified: `WireOpcode` unqualified is this record's own positional property.
		public static uint Wire(Enum method)
		{
			return Core.Network.Protocol.WireOpcode.Of(method);
		}

		// Whether this packet is the PassBlob IIR CARRIER - the one whose body is a length-prefixed IIR record rather
		// than a typed RPC argument list. Almost every extractor opens by filtering on it.
		//
		// The value comes from the method enum, because it already has a name: `PassBlob` c2s
		// (GameAgentMethods) and `DispatchPassBlob` s2c (GameInterfaceMethods) are the two directions of ONE method
		// on ONE conversation, and every method-0 packet in the corpus is GameAgent (0xB171CE13) <-> GameInterface
		// (0x6C67C147) - all 30158 of them. Our own world sends agree: WorldIir.Send and every outbound builder use
		// GameInterfaceMethods.DispatchPassBlob.
		//
		// It is a PROPERTY here rather than a shared constant because the fourteen call sites do not agree on
		// direction - some pin IsDownload, most do not - so naming the direction at each of them would be inventing
		// precision the filter does not have. A bool also works in a property pattern, which a cast expression
		// cannot: `p is { IsIirCarrier: true, SubOpcode: X }`.
		public bool IsIirCarrier => WireOpcode == Wire(GameAgentMethods.PassBlob);

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

		// The IIR RECORD an IsIirCarrier packet carries: the body is `[u32 len][record]`, and a codec owns the record
		// from its type word on - the length word belongs to the framing sender (#530 rule 2), so handing a codec the
		// whole body would fail its `EnsureAtEnd()` by four bytes at the front.
		//
		// This is FRAME work, not decode (§9 D): it slices the carrier, it does not read a field. It exists because
		// wave 8 turned every extractor's `body.AsSpan(4)` into a codec call, and the three spellings of that slice in
		// the importer disagreed on whether to bound by the declared length - which decides whether a codec sees
		// trailing bytes. It is bounded here, once: a record that declares less than the body holds is a record with a
		// tail that is not its own.
		//
		// Empty when the packet cannot carry one (too short, or a header length running past the end).
		public ReadOnlySpan<byte> Record => CapturedRecords.Record(Bytes);
	}
}
