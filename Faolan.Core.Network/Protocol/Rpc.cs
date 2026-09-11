using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Faolan.Core.Crypt;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Protocol
{
	/// <summary>
	///     The structured form of a Conan RPC frame: what a handler builds and hands to the transport, and what the
	///     parser yields for an inbound frame. Serializing it to the wire and parsing it back are inverses, the contract
	///     <c>PacketFrameRoundTripTests</c> locks.
	/// </summary>
	/// <remarks>
	///     The frame is <c>[u32 length][u32 crc32][u32 headerLen][RpcHeader][body]</c>. Every byte of the RpcHeader is
	///     protobuf, and the client's own schema (Faolan.Core/Protobuf/aoc-client-protos.proto, extracted from the
	///     client) names all five of its fields:
	///     <code>
	///     message RpcHeader {                                 wire tag   here
	///       required ProtoEndpoint caller_id = 1;               0x0A     Sender
	///       required ProtoEndpoint endpoint_id = 2;             0x12     Receiver
	///       repeated ProtoEndpoint full_routing_context = 3;    0x1A     RoutingContext
	///       required uint32       method_instance = 4;          0x20     Method
	///       repeated ProtoEndpoint routing_destination = 5;     0x2A     (never observed - Parse throws)
	///     }
	///     </code>
	///     <para>
	///         There is no <c>opcode</c> field and no <c>headerData</c> field. A 16-bit "opcode" reading lands in the
	///         same place for most methods only because of where a varint's groups fall: <c>method_instance</c> is the
	///         tag byte <c>0x20</c> followed by <c>varint(registrationConstant)</c>, so the "opcode" is that tag plus
	///         the varint's first group. See docs/protocol/02-rpc.md.
	///     </para>
	///     <para>
	///         <c>Sender</c> is <c>caller_id</c>; <c>Receiver</c> is the addressed object, whose Type is the interface
	///         hash that names the method against it (see <see cref="ProtocolCatalog" />). <c>Body</c> is everything
	///         after the header.
	///     </para>
	///     <para>
	///         The header is written by hand rather than through the generated <c>RpcHeader</c> in
	///         <c>AocProtocol.g.cs</c>, for three reasons that all follow from this being the frame layer rather than a
	///         message:
	///     </para>
	///     <list type="number">
	///         <item>
	///             <c>IExtensible</c> preserves unknown fields and re-emits them, so a byte nothing accounts for would
	///             ride through the parse, back onto the wire, and read as understood. Read by hand, an unexpected tag
	///             throws (see <see cref="Parse(byte[])" />), which catches a desynced stream at the frame boundary
	///             rather than three subsystems later.
	///         </item>
	///         <item>
	///             The serializer would cost an <c>RpcHeader</c> plus two <c>ProtoEndpoint</c> objects per packet, on a
	///             path every world broadcast takes, to produce bytes this writes directly.
	///         </item>
	///         <item>
	///             Byte-exactness here is the product. protobuf-net's writer is not automatically identical to retail's
	///             - <c>ProtobufExtensions.RepackSingleElementFields</c> exists because it writes a single-element packed
	///             field unpacked - and the header is the one structure every packet carries.
	///         </item>
	///     </list>
	///     <para>
	///         The generated <c>RpcHeader</c> remains the schema of record: it is where the field numbers, names and
	///         cardinalities above come from. This type is written to match it, and
	///         <c>RpcHeaderTests.EveryCapturedFrame_ParsesStrictlyAndReSerializes</c> is what says it does, over every
	///         captured frame in the fixture archive. The same reasoning applies one level down, where
	///         <c>ProtoEndpoint.Read</c> is hand-rolled; the send side of an endpoint does go through protobuf-net, via
	///         <c>ToProtobuf()</c>.
	///     </para>
	/// </remarks>
	public readonly record struct Rpc(ProtoEndpoint Sender, ProtoEndpoint Receiver, uint Method, ReadOnlyMemory<byte> Body)
	{
		// The RpcHeader field tags: field number << 3 | wire type (2 = length-delimited, 0 = varint).
		private const byte CallerIdTag = 0x0A;
		private const byte EndpointIdTag = 0x12;
		private const byte RoutingContextTag = 0x1A;
		private const byte MethodInstanceTag = 0x20;
		private const byte RoutingDestinationTag = 0x2A;

		// The total wire frame length. Stamped by whoever turns the message into (or out of) bytes: Parse on the receive
		// path, and the transport on the send path once the serializer has determined the size. 0 on a freshly built
		// Rpc, whose length is not knowable yet. The packet trace reports it as packetLength in both directions, so it
		// does not have to be threaded alongside the message.
		public int FrameLength { get; init; }

		// `full_routing_context` (field 3) - the endpoint the method actually fires on, when that is not the receiver.
		// Set by exactly one thing: the four login handoffs (ChatServerConnectReady, CSServerConnectReady,
		// LoginConnectReady and OverrideGameTweaks), which address the ServiceConnection and restate PlayerInterface#1
		// here, told apart by Method like every other send.
		//
		// The schema field is `repeated`, but every captured frame carries exactly one, so this models one and Parse
		// throws on a second rather than dropping it.
		public ProtoEndpoint? RoutingContext { get; init; }

		// Body-building overloads: build the body inline with the fluent FaolanStream writers, e.g.
		//   new Rpc(sender, receiver, method, b => b.WriteUInt32(x).WriteArrayUInt32Length(blob))
		// rather than newing and disposing a stream at every call site. The scratch stream is created and disposed
		// here. The Action<FaolanStream> is distinct from the primary constructor's ReadOnlyMemory<byte> Body, so there
		// is no overload ambiguity.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, uint method, Action<FaolanStream> body) : this(sender, receiver, method, BuildBody(body))
		{
		}

		// Enum overloads so call sites pass GameInterfaceMethods.Foo (or PlayerInterfaceMethods.Foo) directly rather
		// than casting every one. Every method enum is `: uint` and its members are the method_instance constant, so
		// this is a widening convert with no arithmetic. Constructors cannot be generic, so the parameter is the boxing
		// base Enum.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum method, Action<FaolanStream> body) : this(sender, receiver, Convert.ToUInt32(method), body)
		{
		}

		// Byte body, for the many sends whose whole body is a single pre-encoded blob or a stream built by a helper.
		// Distinct from the Action<FaolanStream> overloads, since a byte[] is not a delegate.
		public Rpc(ProtoEndpoint sender, ProtoEndpoint receiver, Enum method, ReadOnlyMemory<byte> body) : this(sender, receiver, Convert.ToUInt32(method), body)
		{
		}

		// The record's synthesized ToString prints every readable property one per line; print the message's state on
		// one line instead, with Method in the hex the enums are written in.
		private bool PrintMembers(StringBuilder builder)
		{
			builder.Append($"Sender = {Sender}, Receiver = {Receiver}, Method = 0x{Method:X8}, Body = {Body.Length} bytes, FrameLength = {FrameLength}");
			if (RoutingContext is not null)
				builder.Append($", RoutingContext = {RoutingContext}");

			return true;
		}

		/// <summary>
		///     A fresh reader over the body, for the receive path where handlers read fields sequentially off it. The
		///     send path uses <c>Body</c> directly as immutable memory.
		/// </summary>
		/// <remarks>
		///     Wraps the backing array with no copy when the body is array-backed, which inbound frames always are. A
		///     new cursor per call, so callers keep their own position.
		/// </remarks>
		public FaolanStream BodyReader()
		{
			return MemoryMarshal.TryGetArray(Body, out var segment) && segment.Array is not null
				? new FaolanStream(segment.Array, segment.Offset, segment.Count)
				: new FaolanStream(Body.ToArray());
		}

		/// <summary>
		///     Message to wire frame - the one place a Conan frame is encoded, and the inverse of
		///     <see cref="Parse(byte[])" />.
		/// </summary>
		/// <remarks>
		///     Lays out <c>[u32 length][u32 crc32][u32 headerLen][0x0A caller_id][0x12 endpoint_id]
		///     [0x1A full_routing_context]?[0x20 varint method_instance][body]</c>, with the length and CRC backfilled
		///     once the frame is laid out.
		/// </remarks>
		public byte[] Serialize()
		{
			// Rpc is a struct, so the header lambda cannot capture `this` - every field it writes is copied out here.
			var (sender, receiver, routingContext, method) = (Sender, Receiver, RoutingContext, Method);

			using var stream = new FaolanStream();
			stream.WriteUInt32(0); // length     - backfilled below
			stream.WriteUInt32(0); // crc32      - backfilled below

			// headerLen then the header, from the same writes: the length is measured off the bytes rather than
			// predicted from them, so a new header field does not have to be accounted for twice.
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

		/// <summary>
		///     The independent CRC recompute: a frame's crc32 field must equal Crc32 over everything after it
		///     (<c>Crc32.Calculate</c> skips the leading length and crc).
		/// </summary>
		/// <remarks>
		///     The guardrail <c>PacketFrameRoundTripTests</c> asserts, so a builder that miswrites a single byte fails
		///     there rather than silently on the wire.
		/// </remarks>
		public static bool IsCrcValid(byte[] frame)
		{
			using var stream = new FaolanStream(frame);
			stream.ReadUInt32(); // length
			var crc32 = stream.ReadUInt32();

			return crc32 == Crc32.Calculate(stream);
		}

		/// <summary>
		///     Wire frame to message - the one place a Conan frame is decoded, and the inverse of
		///     <see cref="Serialize" />.
		/// </summary>
		/// <remarks>
		///     The header is read as the protobuf it is, by tag and in any order, and strictly: an unknown or repeated
		///     field, or a missing required one, throws <see cref="InvalidDataException" /> rather than being carried
		///     through as opaque bytes. Nothing needs carrying, because <c>Method</c> holds the whole identity, so
		///     re-serializing a parsed frame reproduces it byte for byte. The receive loop turns
		///     <see cref="InvalidDataException" /> into a logged disconnect, which is the right answer for a header the
		///     wire format says cannot exist.
		///     <para>
		///         The CRC is not re-checked: the live wire is trusted once framed, and capture-derived frames may carry
		///         a placeholder CRC. <see cref="IsCrcValid" /> is the independent recompute the round-trip test uses.
		///     </para>
		/// </remarks>
		public static Rpc Parse(byte[] frame)
		{
			using var stream = new FaolanStream(frame);
			stream.ReadUInt32(); // length - recomputed on serialize
			stream.ReadUInt32(); // crc32 - see IsCrcValid

			using var header = new FaolanStream(stream.ReadArrayUInt32Length());
			ProtoEndpoint? sender = null, receiver = null, routingContext = null;
			uint? method = null;

			// A field that runs past the header's own length is a malformed header, not a programming error: report it as
			// one so the receive loop logs it against the peer and drops the client, like every other framing violation.
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

						// `repeated` in the schema, but every captured frame carries exactly one, so a second is
						// reported rather than dropped.
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

			// ProtoEndpoint.Read starts at the field's own tag - it reads the tag then the length - so rewind the byte
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
