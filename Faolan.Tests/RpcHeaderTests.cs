using System;
using System.IO;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The RpcHeader is protobuf and its method field is ONE varint - not a `[u16 opcode][headerData tail]` pair
	// (Gitea #319/#320/#609/#610). These gates hold the fold in place: that the split was byte-identical (so nothing on
	// the wire moved), that the two bytes a hex dump shows are a derived RENDERING of the method (`WireOpcode`) rather
	// than a field, and that the header is now read strictly rather than carried through as opaque bytes.
	//
	// Since #615 the method enums ARE the `method_instance` constant, so `Rpc.Opcode` is gone entirely and a send
	// carries its member's value with no arithmetic in between - which is what `AMethodEnumMemberIsTheWireField` says.
	//
	// Measured over the corpus: 404,208 of 404,210 field-4 frames carry a well-formed varint whose first group is the
	// dump's low byte. The two misses are not packets - they sit inside a desynced decompressed region of one capture
	// and fail an independent CRC recompute under every alignment (#609).
	public class RpcHeaderTests
	{
		private static readonly ProtoEndpoint Agent = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x08);
		private static readonly ProtoEndpoint Character = Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceType, 0x3BC035EC);

		// What the code used to split into `[u16 opcode][headerData]` is ONE protobuf field: tag 0x20 (field 4, varint)
		// whose value is the method's registration constant, whole. This is the gate that says the two readings are
		// byte-identical, method by method, against the client's own registration tables. It is also why `0x2080` is the
		// boundary and why the old trailing byte was always exactly `0x01`: those are properties of a varint's first
		// group, not of a marker.
		[Fact]
		public void TheOpcodeIsTheMethodVarint()
		{
			foreach (var observed in PlayerAgentMethodIds.ObservedAgentMethods.Cast<Enum>().Concat(PlayerAgentMethodIds.ObservedInterfaceMethods.Cast<Enum>()))
			{
				var method = Convert.ToUInt32(observed);
				var opcode = PlayerAgentMethodIds.ObservedOpcode(method);
				var varint = Varint(method);

				// What a dump reads IS the field tag plus the varint's first group - which is exactly how the header used
				// to be cut, so the observed opcode falls out of the constant with nothing left over.
				Assert.Equal(0x20, opcode >> 8);
				Assert.Equal(varint[0], (byte)(opcode & 0xFF));

				// ...and the frame carries the CONSTANT, not that reading - which is the whole point, since the reading
				// names two methods (#613).
				Assert.Equal(method, Rpc.Parse(new Rpc(Agent, Character, method, ReadOnlyMemory<byte>.Empty).Serialize()).Method);
			}

			// And on an index-registered interface the same field is the plain method index: one group below 128, two
			// above it, which is all the "latency marker" ever was.
			Assert.Equal(new byte[] { 0x09 }, Varint(9));
			Assert.Equal(new byte[] { 0x85, 0x01 }, Varint(133));
		}

		// The half a "constant marker" reading could never account for: the second group appears exactly at method 128
		// and is always exactly 0x01 up to 255, because that is what a varint's second group IS. 0x2080 is the lowest
		// value in the whole corpus whose dump reading carries the byte, and 12,056 sightings are all 0x01.
		[Theory, InlineData(0x00, 1, 0x2000), InlineData(0x7F, 1, 0x207F), InlineData(0x80, 2, 0x2080), InlineData(0x85, 2, 0x2085),
		 InlineData(0x82, 2, 0x2082), InlineData(0xFF, 2, 0x20FF)]
		public void TheContinuationGroupAppearsAtMethod128(uint method, int expectedGroups, int dumpReading)
		{
			var varint = Varint(method);

			Assert.Equal(expectedGroups, varint.Length);
			if (expectedGroups == 2)
				Assert.Equal(0x01, varint[1]);

			// …and the two bytes a dump shows are the tag plus that first group, which is why an index-registered
			// method used to be WRITTEN as `0x2000 + index` (#615 renumbered the enums onto the index itself).
			Assert.Equal((ushort)dumpReading, WireOpcode.Of(method));
			Assert.Equal(varint[0], (byte)(WireOpcode.Of(method) & 0xFF));
		}

		// A send supplies nothing and still carries the whole varint: the pong's method is 130, so the second group goes
		// out without a call site remembering it. Before #609 every method >= 128 that did not hand-pass a `0x01` went
		// out a byte short, and three sites hand-passed one on methods that must not carry it.
		[Fact]
		public void ASendCarriesTheWholeMethodVarint()
		{
			var pong = new Rpc(Agent, Character, GameCharInterfaceMethods.DispatchPong, (byte[])[0x00, 0x00, 0x00, 0x07]);

			Assert.Equal(0x82u, pong.Method);
			Assert.Contains("208201", Convert.ToHexString(pong.Serialize()).ToLowerInvariant());
			Assert.Equal(pong.Method, Rpc.Parse(pong.Serialize()).Method);
		}

		// …and a method below the boundary stays bare, which is the half the trade replies used to get wrong: all seven
		// are methods 0x68-0x6E (104-110) and every one hand-passed a `0x01` retail's captures do not carry.
		[Theory, InlineData(GameCharInterfaceMethods.DispatchRequestTrade), InlineData(GameCharInterfaceMethods.DispatchAcceptedTrade), InlineData(GameCharInterfaceMethods.DispatchPartnerUpdatedCash),
		 InlineData(GameCharInterfaceMethods.DispatchCharacterNameReply)]
		public void ALowIndexSend_StaysOneGroup(GameCharInterfaceMethods method)
		{
			var rpc = new Rpc(Agent, Character, method, ReadOnlyMemory<byte>.Empty);

			Assert.True(rpc.Method < 0x80);
			Assert.Single(Varint(rpc.Method));
		}

		// #792: the two no-arg GameCharInterface methods the server now SENDS carry a zero-length body and one varint
		// group. Both were `Reserved0xNN` until the client's own vtable named what they do, and the corpus pinned when
		// they arrive: `DispatchTradeAbort` answers `CloseShop` (152 of 153 frames) and `0x1B` precedes every
		// `IntendedTeleportIIR` (102 of 102). A body of any length here would be a different message.
		[Theory, InlineData(GameCharInterfaceMethods.DispatchTradeAbort, 0x6A), InlineData(GameCharInterfaceMethods.Reserved0x1B, 0x1B)]
		public void ANoArgWorldMethod_SendsAnEmptyBody(GameCharInterfaceMethods method, int expected)
		{
			var rpc = new Rpc(Agent, Character, method, ReadOnlyMemory<byte>.Empty);

			Assert.Equal((uint)expected, rpc.Method);
			Assert.Single(Varint(rpc.Method));
			Assert.Equal(0, Rpc.Parse(rpc.Serialize()).Body.Length);
		}

		// The collision #608 is about, stated as bytes: the SAME opcode with two different method varints is two
		// different methods, and only the whole field tells them apart. Retail sends all four; an opcode-keyed dispatch
		// would read an 8-byte `AuthenticateLoggedIn` as the no-arg `ReceivedCharacterSettings` ack.
		[Theory, InlineData(PlayerAgentMethods.ReceivedCharacterSettings, 0x20EC), InlineData(PlayerAgentMethods.AuthenticateLoggedIn, 0x20EC),
		 InlineData(PlayerAgentMethods.VerifyLanguageSetting, 0x20D6), InlineData(PlayerAgentMethods.UpdateCharacterPreview, 0x20D6)]
		public void OneOpcode_CanNameTwoMethods(PlayerAgentMethods method, int opcode)
		{
			var frame = new Rpc(Agent, Endpoints.Endpoint(RpcInterfaces.PlayerAgentType, 0x02), method, ReadOnlyMemory<byte>.Empty).Serialize();
			var parsed = Rpc.Parse(frame);

			// Both members of a pair READ the same in a dump...
			Assert.Equal((ushort)opcode, PlayerAgentMethodIds.ObservedOpcode(parsed.Method));

			// ...and the method is what tells them apart - it is the only identity the frame has (#613).
			Assert.Equal(method, (PlayerAgentMethods)parsed.Method);
		}

		// The four login handoffs, whole: header field 3 (`full_routing_context`) restating PlayerInterface#1, then the
		// method constant. The expected bytes are the literals the handoff shipped before the fold, which are the
		// captured retail bytes (118 sightings each) - so the construction is proven against retail, not against itself.
		// The codebase used to read all four as one invented opcode `0x1A07` with an opaque 13-byte "session blob";
		// `0x1A 0x07` is field 3's tag plus its length, and the blob is the endpoint plus the method (#609/#610).
		[Theory, InlineData(PlayerInterfaceMethods.ChatServerConnectReady, "1a070d3857157d100120fae5989d02"), InlineData(PlayerInterfaceMethods.CSServerConnectReady, "1a070d3857157d100120e0d4b4d705"),
		 InlineData(PlayerInterfaceMethods.LoginConnectReady, "1a070d3857157d100120f9d8d9c50d"), InlineData(PlayerInterfaceMethods.OverrideGameTweaks, "1a070d3857157d100120e3ee9fe207")]
		public void TheLoginHandoffs_ReproduceTheCapturedHeaders(PlayerInterfaceMethods method, string expected)
		{
			var rpc = new Rpc(Endpoints.Endpoint(RpcInterfaces.PlayerAgentServiceConnectionType, 0x08), Endpoints.Endpoint(RpcInterfaces.PlayerInterfaceType, 0x01), method, ReadOnlyMemory<byte>.Empty)
			{
				RoutingContext = Endpoints.Endpoint(RpcInterfaces.PlayerInterfaceType, 0x01)
			};

			Assert.EndsWith(expected, Convert.ToHexString(rpc.Serialize()).ToLowerInvariant());

			// …and it survives the round trip as the same method plus the same routing context, rather than as bytes.
			var parsed = Rpc.Parse(rpc.Serialize());
			Assert.Equal(method, (PlayerInterfaceMethods)parsed.Method);
			Assert.Equal(RpcInterfaces.PlayerInterfaceType, parsed.RoutingContext!.Type);
			Assert.Equal(1u, parsed.RoutingContext.Instance);
		}

		// The fold's payoff: there is nothing left for a caller to supply, so Parse can be strict. A header that is not
		// the protobuf the schema says it is gets rejected at the frame boundary (the receive loop logs it and drops the
		// client) instead of being carried through as an opaque tail that re-serializes a lie.
		[Fact]
		public void Parse_RejectsAHeaderItCannotAccountFor()
		{
			var valid = new Rpc(Agent, Character, GameCharInterfaceMethods.DispatchPong, ReadOnlyMemory<byte>.Empty).Serialize();
			Assert.Equal(0x82u, Rpc.Parse(valid).Method);

			// A trailing byte after the method varint: it belonged to no field, and used to be kept verbatim as
			// "headerData" - which is how a spurious 0x01 rode the trade replies for years without anything noticing.
			Assert.Throws<InvalidDataException>(() => Rpc.Parse(WithHeaderSuffix(valid, [0x7F])));

			// A field the schema has but no captured frame carries (routing_destination, field 5).
			Assert.Throws<InvalidDataException>(() => Rpc.Parse(WithHeaderSuffix(valid, [0x2A, 0x00])));

			// The method varint truncated mid-continuation - the failure mode the old split could NOT survive, since a
			// missing group silently renames the method. `20 82 01` minus its last byte.
			Assert.Throws<InvalidDataException>(() => Rpc.Parse(WithoutHeaderSuffix(valid, 1)));

			// A header with no method_instance at all: the two endpoints and nothing else.
			Assert.Throws<InvalidDataException>(() => Rpc.Parse(WithoutHeaderSuffix(valid, 3)));
		}

		// The strictness, against RETAIL. Every captured frame in the fixture archive must parse under the strict header
		// reader and re-serialize to the bytes it arrived as. This is the gate that says the fold is safe on real
		// traffic rather than on frames the test built: a header shape the corpus carries and the reader rejects fails
		// here, and so does any field the reader accepts but cannot write back.
		//
		// Compared from offset 8 - past the length and the crc32 - because the corpus stores REBUILT packets whose CRC
		// is a placeholder (TsharkCaptureReader.Rebuild); the header and body are the captured bytes, and the header is
		// what this change touches.
		[Fact]
		// ⚠️ **"Every captured frame" means every frame IN THE ARCHIVE SLICE, and that slice cannot see one whole
		// class of defect.** `CorpusExtract` carries IIR-carrier bodies only - MoveTaskIIR, SimpleCharFullUpdateIIR,
		// MagicBuffIIR and the rest - and every one of those rides `PassBlob`, whose `method_instance` is **0**. A
		// zero needs one varint group, so a bug in how the header's method VARINT is written is structurally
		// invisible here.
		//
		// It was invisible: the importer's capture reader rebuilt that field from the dump's two-byte "Opcode",
		// which is the tag plus the varint's FIRST GROUP only, and 18,882 of 544,386 captured frames - every Ping
		// and Pong, the whole hash-registered login tier - therefore ended mid-varint and were refused by this very
		// parser. This test passed throughout (#881).
		//
		// The whole-corpus counterpart is `--report frame-health`, which parses every frame in the real dumps rather
		// than the slice. Keep both: this one guards the round-trip, that one guards the population.
		public void EveryCapturedFrame_ParsesStrictlyAndReSerializes()
		{
			var frames = 0;
			foreach (var entry in CorpusExtract.All())
			{
				var parsed = Rpc.Parse(entry.Full);

				// The archive records the two bytes the dump read for the method field. They are a RENDERING of the
				// parsed method, so recomputing them from it reproduces the archived value on every frame - including
				// the hash-registered pair, where that rendering is all a dump can show and is not an identity (#613).
				// The archived value stays in wire space: fixtures.bin cannot be regenerated without the private
				// corpus, so #615 converts at the comparison rather than redefining the stored field.
				Assert.Equal((ushort)entry.Opcode, WireOpcode.Of(parsed.Method));

				Assert.Equal(entry.Full[8..], parsed.Serialize()[8..]);
				frames++;
			}

			// The corpus is a ratchet (CorpusExtract.ExpectedCounts); this only guards against the loop going vacuous.
			Assert.True(frames > 40_000, $"expected the whole corpus, walked only {frames} frames");
		}

		// #615, stated: a `*Methods` member IS the `method_instance` this frame writes, on BOTH kinds of class, with no
		// base added or subtracted anywhere. That is what let `Rpc.Opcode`, `HasOpcode`, `MethodIndexBase` and the
		// ushort constructor overloads all delete - and it is what makes a dispatch `switch ((FooMethods)packet.Method)`
		// rather than arithmetic. Asserted end to end (build -> serialize -> parse), so a conversion sneaking back in
		// anywhere on that path fails here.
		[Fact]
		public void AMethodEnumMemberIsTheWireField()
		{
			foreach (var method in new Enum[]
			         {
				         GameCharInterfaceMethods.DispatchPong, GameCharInterfaceMethods.DispatchAddItem, GameAgentMethods.PassBlob,
				         CSPlayerAgentMethods.Authenticate, TradepostInterfaceMethods.DispatchMailMessage,
				         PlayerInterfaceMethods.AuthenticateAck, PlayerAgentMethods.LoginCharacter
			         })
			{
				var rpc = new Rpc(Agent, Character, method, ReadOnlyMemory<byte>.Empty);

				Assert.Equal(Convert.ToUInt32(method), rpc.Method);
				Assert.Equal(Convert.ToUInt32(method), Rpc.Parse(rpc.Serialize()).Method);
			}

			// Every method enum is uint-backed for the same reason: the member is the field, and the field is a uint32.
			foreach (var type in typeof(GameCharInterfaceMethods).Assembly.GetTypes())
			{
				if (type is { IsEnum: true, Namespace: "Faolan.Core.Enums" } && type.Name.EndsWith("Methods", StringComparison.Ordinal))
					Assert.Equal(typeof(uint), Enum.GetUnderlyingType(type));
			}
		}

		// Rewrites the frame's header to `header || suffix`, keeping the length/CRC honest - the only way to build a
		// header the serializer would never emit.
		private static byte[] WithHeaderSuffix(byte[] frame, byte[] suffix)
		{
			return Reframe(frame, header => [..header, ..suffix]);
		}

		private static byte[] WithoutHeaderSuffix(byte[] frame, int bytes)
		{
			return Reframe(frame, header => header[..^bytes]);
		}

		private static byte[] Reframe(byte[] frame, Func<byte[], byte[]> rewrite)
		{
			using var source = new FaolanStream(frame);
			source.ReadUInt32();
			source.ReadUInt32();
			var header = rewrite(source.ReadArrayUInt32Length());
			var body = source.ReadRemainingArray();

			using var built = new FaolanStream();
			built.WriteUInt32(0);
			built.WriteUInt32(0);
			built.WriteUInt32((uint)header.Length);
			built.WriteArray(header);
			built.WriteArray(body);

			var bytes = built.ToArray();
			bytes.WriteU32Be(0, (uint)(bytes.Length - sizeof(uint)));
			return bytes;
		}

		// Base-128 varint, low group first - the encoding FaolanStream.WriteVarint emits, restated here so the gate
		// checks the bytes rather than the writer against itself.
		private static byte[] Varint(uint value)
		{
			var bytes = new byte[5];
			var length = 0;
			while (true)
			{
				var group = (byte)(value & 0x7F);
				value >>= 7;
				if (value == 0)
				{
					bytes[length++] = group;
					return bytes[..length];
				}

				bytes[length++] = (byte)(group | 0x80);
			}
		}
	}
}
