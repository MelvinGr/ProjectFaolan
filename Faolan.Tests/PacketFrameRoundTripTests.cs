using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Xunit;

namespace Faolan.Tests
{
	// GUARDRAIL for the structured-packet-pipeline epic (Gitea #406, phase 0). Locks the CURRENT wire frame
	// contract - [u32 length][u32 crc32][u32 headerLen][sender endpoint][receiver endpoint][u16 opcode][headerData][body]
	// - as the migration baseline, by proving the builder (PacketStream.WriteHeader + WriteLengthHash) and the
	// parser (ConanPacket) are mutual inverses, cross-checked by the parser's INDEPENDENT crc recompute (IsValid).
	// The future PacketSerializer.Serialize / PacketParser.Parse (phase 2) must satisfy these exact assertions, so a
	// rewrite that changes a single frame byte fails here rather than silently on the wire.
	public class PacketFrameRoundTripTests
	{
		// (senderType, senderInstance, receiverType, receiverInstance, opcode, headerDataHex, bodyHex)
		// The two keepalive replies from KeepAliveService are the seed cases (GameServerStats on GameInterface with no
		// headerData; Pong on GameCharInterface with a 1-byte headerData) plus a header-data-heavy control case.
		[Theory]
		[InlineData(Endpoints.GameAgentType, 0x08u, Endpoints.GameInterfaceType, 0x3BC035ECu,
			(ushort)GameInterfaceOpcodes.DispatchGameServerStats, "", "42B32A07000000000000000000000000")]
		[InlineData(Endpoints.GameCharAgentType, 0x08u, Endpoints.GameCharInterfaceType, 0x3BC035ECu,
			(ushort)GameCharInterfaceOpcodes.DispatchPong, "01", "0000000700000012AF50A848")]
		[InlineData(Endpoints.GameInterfaceType, 0x01u, Endpoints.GameCharInterfaceType, 0xDEADBEEFu,
			(ushort)GameCharInterfaceOpcodes.DispatchPong, "0D385715", "")]
		public void Frame_Builder_And_Parser_AreMutualInverses(uint senderType, uint senderInstance, uint receiverType, uint receiverInstance, ushort opcode, string headerDataHex, string bodyHex)
		{
			var headerData = Convert.FromHexString(headerDataHex);
			var body = Convert.FromHexString(bodyHex);

			var frame = BuildFrame(senderType, senderInstance, receiverType, receiverInstance, opcode, headerData, body);

			// Parse it back with the INBOUND parser and assert every field survives, including the CRC (recomputed
			// independently by ConanPacket) - so this is not a build-echoes-itself tautology.
			var parsed = new ConanPacket(frame);
			Assert.True(parsed.IsValid, "crc32 written by the builder must validate under the parser's independent recompute");
			Assert.Equal((uint)frame.Length - sizeof(uint), parsed.Length); // length prefix excludes its own 4 bytes
			Assert.Equal(senderType, parsed.Sender.Type);
			Assert.Equal(senderInstance, parsed.Sender.Instance);
			Assert.Equal(receiverType, parsed.Receiver.Type);
			Assert.Equal(receiverInstance, parsed.Receiver.Instance);
			Assert.Equal(opcode, parsed.Opcode);
			Assert.Equal(headerData, parsed.HeaderData);
			Assert.Equal(body, parsed.Body.ToArray());

			// Re-serialize from the parsed fields and assert byte-for-byte equality with the original frame - the
			// Serialize(Parse(bytes)) == bytes oracle, expressed with today's types.
			var reserialized = BuildFrame(parsed.Sender.Type, parsed.Sender.Instance, parsed.Receiver.Type, parsed.Receiver.Instance, parsed.Opcode,
				parsed.HeaderData, parsed.Body.ToArray());
			Assert.Equal(frame, reserialized);

			// Same oracle through the NEW structured functions (phase 2): PacketParser.Parse and PacketSerializer.Serialize
			// must be inverses AND byte-identical to the frame the current builder produced.
			var rpc = PacketParser.Parse(frame);
			Assert.Equal(senderType, rpc.Sender.Type);
			Assert.Equal(receiverType, rpc.Receiver.Type);
			Assert.Equal(opcode, rpc.Opcode);
			Assert.Equal(headerData, rpc.HeaderData.ToArray());
			Assert.Equal(body, rpc.Body.ToArray());
			Assert.Equal(frame, PacketSerializer.Serialize(rpc));
		}

		// Endpoints.EndpointFromBytes must reconstruct a ProtoEndpoint that re-serializes to the EXACT input bytes, so
		// the hand-rolled capture-literal endpoints (the UniverseAgent InitAuth receiver, and the old echo-receiver it
		// replaced) survive the migration to the structured Rpc receiver byte-for-byte.
		[Theory]
		[InlineData("0d5440380c1099a742")] // InitAuth receiver (instance 0x109399)
		[InlineData("0d5440380c10eceb80de03")] // the old hardcoded echo receiver (larger instance)
		public void EndpointFromBytes_RoundTripsToTheSameBytes(string hex)
		{
			var bytes = Convert.FromHexString(hex);
			Assert.Equal(bytes, Endpoints.EndpointFromBytes(bytes).ToProtobuf());
		}

		// Builds a whole wire frame exactly as PacketStream.SendAsync does: WriteHeader (+ body) then WriteLengthHash
		// (backfills the length + crc32), then ToArray.
		private static byte[] BuildFrame(uint senderType, uint senderInstance, uint receiverType, uint receiverInstance, ushort opcode, byte[] headerData, byte[] body)
		{
			var packet = new PacketStream();
			packet.WriteHeader(Endpoints.Endpoint(senderType, senderInstance), Endpoints.Endpoint(receiverType, receiverInstance), opcode,
				headerData.Length > 0 ? headerData : null);
			packet.WriteArray(body);
			packet.WriteLengthHash();
			return packet.ToArray();
		}
	}
}
