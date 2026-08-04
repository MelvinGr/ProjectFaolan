using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Faolan.Core.Crypt;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Protocol
{
	// The structured form of a Conan RPC frame - what a handler builds and hands to the transport, and what the parser
	// yields for an inbound frame - instead of a raw byte[] that has to be re-parsed to be logged or inspected.
	// Serializing this to the wire and parsing it back are inverses (the contract PacketFrameRoundTripTests locks).
	// Introduced for the structured-packet-pipeline epic (Gitea #406).
	//
	// ## The header, and why this type has no "opcode" field (Gitea #610)
	//
	//     [u32 length][u32 crc32][u32 headerLen][RpcHeader][body]
	//
	// Every byte of the RpcHeader is protobuf, and the client's own schema (Faolan.Core/Protobuf/aoc-client-protos.proto,
	// extracted from the client) names all five of its fields:
	//
	//     message RpcHeader {                                 wire tag   here
	//       required ProtoEndpoint caller_id = 1;               0x0A     Sender
	//       required ProtoEndpoint endpoint_id = 2;             0x12     Receiver
	//       repeated ProtoEndpoint full_routing_context = 3;    0x1A     RoutingContext
	//       required uint32       method_instance = 4;          0x20     Method
	//       repeated ProtoEndpoint routing_destination = 5;     0x2A     (never observed - Parse throws)
	//     }
	//
	// ⚠️ **There is no `opcode` field and no `headerData` field.** For years this header was read as
	// `[u16 opcode][optional headerData tail]`, which lands in the same place for most methods only because of where a
	// varint's groups fall: `method_instance` is the tag byte `0x20` followed by `varint(registrationConstant)`, so the
	// "opcode" was that tag plus the varint's FIRST group and the "headerData" was the remaining groups. Every oddity
	// the old reading had to list as a special case falls out of the varint (docs/protocol/02-rpc.md, #319/#320/#609):
	// the `0x01` "latency marker" is a second varint group, `0x2080` is the boundary because that is where a constant
	// first needs two groups, and "opcode `0x1A07` + a 13-byte session blob" was field 3's tag plus its length byte.
	//
	// Sender = caller_id; Receiver = the addressed object - its Type is the interface hash, which names the method
	// against it (see ProtocolCatalog). Body is everything after the header.
	//
	// ## Why the header is written by hand, when `AocProtocol.g.cs` has a generated `RpcHeader`
	//
	// The schema above IS in the generated model, and its `ProtoEndpoint` is the very type this record holds - so
	// `Serializer.Serialize(stream, new RpcHeader { … })` would work, and would be the obvious move. Three reasons it
	// is not what runs here, all of them about this being the FRAME layer rather than a message:
	//
	// 1. **`IExtensible` preserves unknown fields and re-emits them.** That is exactly the behaviour #610 removed: a
	//    byte nothing accounts for rides through the parse, back onto the wire, and reads as "we understood it". The
	//    old `headerData` tail was that bug with a name; protobuf-net's extension data would be the same bug without
	//    one. Read by hand, an unexpected tag throws (see Parse) - which is how a desynced stream gets caught at the
	//    frame boundary instead of three subsystems later.
	// 2. **`IExtensible` aside, the serializer would still cost an `RpcHeader` plus two `ProtoEndpoint` objects per
	//    packet** on a path every world broadcast takes, to produce bytes this writes directly. (The frame needs
	//    `headerLen` before the header, but that is not the reason: `WriteArrayUInt32Length` measures the header off
	//    its own writes, which is strictly better than the hand-summed arithmetic that used to live here.)
	// 3. **Byte-exactness here is the product, not a nicety.** protobuf-net's writer is not automatically identical to
	//    retail's: `ProtobufExtensions.RepackSingleElementFields` exists because it writes a single-element packed
	//    field unpacked, one byte short of the capture (#538). Nothing in RpcHeader is packed, so that specific trap
	//    does not apply - but the header is the one structure every packet carries, and it is cheaper to own five
	//    tag bytes than to own a workaround for whatever the encoder does next.
	//
	// The generated `RpcHeader` stays the SCHEMA of record - it is where the field numbers, names and cardinalities in
	// the table above come from, and `aoc-client-protos.proto` is extracted from the client. This type is written to
	// match it, and `RpcHeaderTests.EveryCapturedFrame_ParsesStrictlyAndReSerializes` is what says it does, over every
	// captured frame in the fixture archive. (The same reasoning already applies one level down: `ProtoEndpoint.Read`
	// is hand-rolled "instead of a generic protobuf tree walk + protobuf-net round-trip - the endpoint wire is fixed
	// and tiny". Note the SEND side of an endpoint does go through protobuf-net, via `ToProtobuf()`.)
	public readonly record struct Rpc(ProtoEndpoint Sender, ProtoEndpoint Receiver, uint Method, ReadOnlyMemory<byte> Body)
	{
		// The RpcHeader field tags: field number << 3 | wire type (2 = length-delimited, 0 = varint).
		private const byte CallerIdTag = 0x0A;
		private const byte EndpointIdTag = 0x12;
		private const byte RoutingContextTag = 0x1A;
		private const byte MethodInstanceTag = 0x20;
		private const byte RoutingDestinationTag = 0x2A;

		// The total wire frame length. Stamped by whoever turns the message into (or out of) bytes: Parse
		// on the receive path, and the transport on the send path once the serializer has determined the size
		// (`rpc with { FrameLength = … }`). 0 on a freshly built Rpc, whose length isn't knowable yet. The packet trace
		// reports it as packetLength in BOTH directions, so it doesn't have to be threaded alongside the message.
		public int FrameLength { get; init; }

		// `full_routing_context` (field 3) - the endpoint the method actually fires on, when that is not the receiver.
		// Set by exactly one thing: the four login handoffs, which address the ServiceConnection and restate
		// PlayerInterface#1 here. Those are the 472 captured frames the codebase used to call "opcode 0x1A07 with an
		// opaque 13-byte session blob" (#609/#610); decoded they are ChatServerConnectReady, CSServerConnectReady,
		// LoginConnectReady and OverrideGameTweaks, told apart by Method like every other send.
		// The schema field is `repeated`, but every captured frame carries exactly one, so this models one and Parse
		// throws on a second rather than dropping it.
		public ProtoEndpoint? RoutingContext { get; init; }

		// Body-building overloads: build the body inline with the fluent FaolanStream writers, e.g.
		//   new Rpc(sender, receiver, opcode, b => b.WriteUInt32(x).WriteArrayUInt32Length(blob))
		// instead of newing + disposing a stream at every call site. The scratch stream is created and DISPOSED here
		// (unlike the old fluent PacketStream send pattern). The Action<FaolanStream> is distinct from the primary
		// ctor's ReadOnlyMemory<byte> Body, so there is no overload ambiguity.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, uint method, Action<FaolanStream> body) : this(sender, receiver, method, BuildBody(body))
		{
		}

		// Enum overloads so call sites pass GameInterfaceMethods.Foo (or PlayerInterfaceMethods.Foo) directly instead
		// of casting every one. Every method enum is `: uint` and its members ARE the method_instance constant (#615),
		// so this is a widening convert with no arithmetic. Constructors can't be generic, so the param is the boxing
		// base Enum - one negligible box per packet.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum method, Action<FaolanStream> body) : this(sender, receiver, Convert.ToUInt32(method), body)
		{
		}

		// Byte body - for the many sends whose whole body is a single pre-encoded blob (codec.Build()) or a stream built
		// by a helper. Distinct from the Action<FaolanStream> overloads (a byte[] is not a delegate).
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum method, ReadOnlyMemory<byte> body) : this(sender, receiver, Convert.ToUInt32(method), body)
		{
		}

		// The record's SYNTHESIZED ToString prints every readable property one per line; print the message's STATE on
		// one line instead, with Method in the hex the enums are written in.
		private bool PrintMembers(StringBuilder builder)
		{
			builder.Append($"Sender = {Sender}, Receiver = {Receiver}, Method = 0x{Method:X8}, Body = {Body.Length} bytes, FrameLength = {FrameLength}");
			if (RoutingContext is not null)
				builder.Append($", RoutingContext = {RoutingContext}");

			return true;
		}

		// A fresh reader over the body, for the RECEIVE path (handlers read fields sequentially off it). The send path
		// uses Body directly as immutable memory. Wraps the backing array with NO copy when the body is array-backed
		// (always, for our inbound frames); a new cursor per call, so callers keep their own position.
		public FaolanStream BodyReader()
		{
			return MemoryMarshal.TryGetArray(Body, out var segment) && segment.Array is not null
				? new FaolanStream(segment.Array, segment.Offset, segment.Count)
				: new FaolanStream(Body.ToArray());
		}

		// Message -> wire frame. The ONE place a Conan frame is encoded, and the inverse of Parse (the contract
		// PacketFrameRoundTripTests locks): [u32 length][u32 crc32][u32 headerLen][0x0A caller_id][0x12 endpoint_id]
		// [0x1A full_routing_context]?[0x20 varint method_instance][body], with the length and CRC backfilled once the
		// frame is laid out.
		public byte[] Serialize()
		{
			// Rpc is a struct, so the header lambda cannot capture `this` - every field it writes is copied out here.
			var (sender, receiver, routingContext, method) = (Sender, Receiver, RoutingContext, Method);

			using var stream = new FaolanStream();
			stream.WriteUInt32(0); // length     - backfilled below
			stream.WriteUInt32(0); // crc32      - backfilled below

			// headerLen then the header, from the SAME writes - the length is measured off the bytes rather than
			// predicted from them. It used to be hand-summed ("each endpoint field costs [tag][byteLen] + its bytes,
			// and method_instance [tag] + its varint", plus a VarintLength helper existing only to feed it), which is
			// a second, arithmetic description of the header that a new field has to be remembered in twice - and the
			// endpoints had to be pre-serialized to feed it, which is why they went in as byte[] rather than as the
			// messages WriteProtobufByteLength takes.
			stream.WriteArrayUInt32Length(header =>
			{
				header.WriteByte(CallerIdTag);
				header.WriteProtobufByteLength(sender);
				header.WriteByte(EndpointIdTag);
				header.WriteProtobufByteLength(receiver);
				if (routingContext is not null)
				{
					header.WriteByte(RoutingContextTag);
					header.WriteProtobufByteLength(routingContext);
				}

				header.WriteByte(MethodInstanceTag);
				header.WriteVarint(method);
			});

			stream.WriteArray(Body.Span);

			// Backfill the length (everything after the length field) and the CRC over the whole frame.
			var end = stream.Position;
			stream.Position = 0;
			stream.WriteUInt32(stream.Length - sizeof(uint));
			stream.WriteUInt32(Crc32.Calculate(stream));
			stream.Position = end;

			return stream.ToArray();
		}

		// The INDEPENDENT crc recompute: a frame's crc32 field must equal Crc32 over everything after it (Crc32.Calculate
		// skips the leading length+crc). The guardrail PacketFrameRoundTripTests asserts, so a builder that miswrites a
		// single byte fails there rather than silently on the wire.
		public static bool IsCrcValid(byte[] frame)
		{
			using var stream = new FaolanStream(frame);
			stream.ReadUInt32(); // length
			var crc32 = stream.ReadUInt32();

			return crc32 == Crc32.Calculate(stream);
		}

		// Wire frame -> message. The ONE place a Conan frame is decoded, and the inverse of Serialize.
		//
		// The header is read as the protobuf it is - by TAG, in any order - and STRICTLY: an unknown or repeated field,
		// or a missing required one, throws InvalidDataException rather than being carried through as opaque bytes.
		// That is affordable because there is nothing left to carry: Method holds the whole identity, so re-serializing
		// a parsed frame reproduces it byte for byte (which the old `[u16 opcode][tail]` split could not do, and paid
		// for with a raw-tail override field). The receive loop already turns InvalidDataException into a logged
		// disconnect (NetworkClient.ReceiveLoopAsync), which is the right answer for a header the wire format says
		// cannot exist.
		//
		// The CRC is NOT re-checked (the live wire is trusted once framed, and capture-derived frames may carry a
		// placeholder CRC); IsCrcValid is the independent recompute the round-trip test uses.
		public static Rpc Parse(byte[] frame)
		{
			using var stream = new FaolanStream(frame);
			stream.ReadUInt32(); // length - recomputed on serialize
			stream.ReadUInt32(); // crc32 - see IsCrcValid

			using var header = new FaolanStream(stream.ReadArrayUInt32Length());
			ProtoEndpoint? sender = null, receiver = null, routingContext = null;
			uint? method = null;

			// A field that runs past the header's own length is a malformed header, not a programming error: report it as
			// one so the receive loop logs it against the PEER and drops the client, like every other framing violation.
			try
			{
				ReadFields();
			}
			catch (EndOfStreamException ex)
			{
				throw new InvalidDataException("RpcHeader ended mid-field: a length or varint runs past the header's own length.", ex);
			}

			if (sender is null || receiver is null || method is null)
				throw new InvalidDataException("Incomplete RpcHeader: caller_id, endpoint_id and method_instance are all required.");

			return new Rpc(sender, receiver, method.Value, stream.ReadRemainingArray())
			{
				RoutingContext = routingContext,
				FrameLength = frame.Length
			};

			void ReadFields()
			{
				while (header.Position < header.Length)
				{
					var at = header.Position;
					var tag = header.ReadByte();
					switch (tag)
					{
						case CallerIdTag:
						{
							sender = sender is null ? ReadEndpoint(header) : throw Repeated("caller_id", at);
							break;
						}

						case EndpointIdTag:
						{
							receiver = receiver is null ? ReadEndpoint(header) : throw Repeated("endpoint_id", at);
							break;
						}

						// `repeated` in the schema, but all 472 captured frames carry exactly one, so a second is
						// something we have never seen and says so rather than being dropped.
						case RoutingContextTag:
						{
							routingContext = routingContext is null ? ReadEndpoint(header) : throw Repeated("full_routing_context", at);
							break;
						}

						case MethodInstanceTag:
						{
							if (method is not null)
								throw Repeated("method_instance", at);

							var value = header.ReadVarint();
							if (value > uint.MaxValue)
								throw new InvalidDataException($"RpcHeader method_instance {value} at offset {at} overflows the schema's uint32.");

							method = (uint)value;
							break;
						}

						case RoutingDestinationTag:
						{
							throw new InvalidDataException($"RpcHeader carries routing_destination (field 5) at offset {at}, which no captured frame does.");
						}

						default:
						{
							throw new InvalidDataException($"Unexpected RpcHeader field tag 0x{tag:X2} at offset {at}.");
						}
					}
				}
			}

			// ProtoEndpoint.Read starts at the field's own tag (it reads the tag then the length), so rewind the byte
			// the tag switch consumed.
			static ProtoEndpoint ReadEndpoint(FaolanStream header)
			{
				header.Position--;
				return ProtoEndpoint.Read(header);
			}

			static InvalidDataException Repeated(string field, uint at)
			{
				return new InvalidDataException($"RpcHeader carries {field} twice (second at offset {at}).");
			}
		}

		public static Rpc Parse(ReadOnlySpan<byte> frame)
		{
			return Parse(frame.ToArray());
		}

		private static byte[] BuildBody(Action<FaolanStream> body)
		{
			using var stream = new FaolanStream();
			body(stream);
			return stream.ToArray();
		}
	}
}
