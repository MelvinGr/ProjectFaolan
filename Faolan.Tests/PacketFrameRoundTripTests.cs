using Faolan.Core.Constants;
using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Xunit;

namespace Faolan.Tests
{
	// GUARDRAIL for the structured-packet-pipeline epic (Gitea #406). Locks the wire frame contract -
	// [u32 length][u32 crc32][u32 headerLen][caller_id][endpoint_id][full_routing_context?][method_instance][body] - by
	// proving the serializer (Rpc.Serialize) and the parser (Rpc.Parse) are mutual
	// inverses, cross-checked by the parser's INDEPENDENT crc recompute (IsValid). A rewrite that changes a single
	// frame byte fails here rather than silently on the wire.
	public class PacketFrameRoundTripTests
	{
		// Endpoints.EndpointFromBytes must reconstruct a ProtoEndpoint that re-serializes to the EXACT input bytes, so
		// the hand-rolled capture-literal endpoints (the UniverseAgent InitAuth receiver, and the old echo-receiver it
		// replaced) survive the migration to the structured Rpc receiver byte-for-byte.
		[Theory, InlineData("0d5440380c1099a742"), InlineData("0d5440380c10eceb80de03")]
		// InitAuth receiver (instance 0x109399)
			// the old hardcoded echo receiver (larger instance)
		public void EndpointFromBytes_RoundTripsToTheSameBytes(string hex)
		{
			var bytes = Convert.FromHexString(hex);
			Assert.Equal(bytes, Endpoints.EndpointFromBytes(bytes).ToProtobuf());
		}

		// (senderType, senderInstance, receiverType, receiverInstance, method, routingContextType, bodyHex)
		// The two keepalive replies from KeepAliveService are the seed cases (GameServerStats, a one-group method;
		// Pong, whose method needs a second varint group - what used to be read as a 1-byte "headerData"), plus a
		// 32-bit hashed method and a login handoff, which is the only shape that carries a full_routing_context.
		[Theory, InlineData(RpcInterfaces.GameAgentType, 0x08u, RpcInterfaces.GameInterfaceType, 0x3BC035ECu, (uint)GameInterfaceMethods.DispatchGameServerStats, 0u,
			 "42B32A07000000000000000000000000"),
		 InlineData(RpcInterfaces.GameCharAgentType, 0x08u, RpcInterfaces.GameCharInterfaceType, 0x3BC035ECu, (uint)GameCharInterfaceMethods.DispatchPong, 0u, "0000000700000012AF50A848"),
		 InlineData(RpcInterfaces.PlayerAgentType, 0x02u, RpcInterfaces.PlayerInterfaceType, 0x01u, (uint)PlayerInterfaceMethods.AuthenticateAck, 0u, "00000001"),
		 InlineData(RpcInterfaces.PlayerAgentServiceConnectionType, 0x08u, RpcInterfaces.PlayerInterfaceType, 0x01u, (uint)PlayerInterfaceMethods.LoginConnectReady, RpcInterfaces.PlayerInterfaceType, "DEADBEEF")]
		public void Frame_Builder_And_Parser_AreMutualInverses(uint senderType, uint senderInstance, uint receiverType, uint receiverInstance, uint method, uint routingContextType, string bodyHex)
		{
			var body = Convert.FromHexString(bodyHex);

			var frame = BuildFrame(senderType, senderInstance, receiverType, receiverInstance, method, routingContextType, body);

			// Parse it back with the INBOUND parser and assert every field survives, including the CRC (recomputed
			// independently by Rpc.IsCrcValid) - so this is not a build-echoes-itself tautology.
			var parsed = Rpc.Parse(frame);
			Assert.True(Rpc.IsCrcValid(frame), "crc32 written by the builder must validate under the parser's independent recompute");
			Assert.Equal(frame.Length, parsed.FrameLength);
			Assert.Equal(senderType, parsed.Sender.Type);
			Assert.Equal(senderInstance, parsed.Sender.Instance);
			Assert.Equal(receiverType, parsed.Receiver.Type);
			Assert.Equal(receiverInstance, parsed.Receiver.Instance);
			Assert.Equal(method, parsed.Method);
			Assert.Equal(routingContextType, parsed.RoutingContext?.Type ?? 0u);
			Assert.Equal(body, parsed.Body.ToArray());

			// Re-serialize from the parsed fields and assert byte-for-byte equality with the original frame - the
			// Serialize(Parse(bytes)) == bytes oracle. This is what the header's old opaque `headerData` tail bought,
			// and what carrying the method whole gives for free (Gitea #610).
			Assert.Equal(frame, parsed.Serialize());
		}

		// Builds a whole wire frame through the production serializer.
		private static byte[] BuildFrame(uint senderType, uint senderInstance, uint receiverType, uint receiverInstance, uint method, uint routingContextType, byte[] body)
		{
			return new Rpc(Endpoints.Endpoint(senderType, senderInstance), Endpoints.Endpoint(receiverType, receiverInstance), method, body)
			{
				RoutingContext = routingContextType == 0
					? null
					: Endpoints.Endpoint(routingContextType, 0x01)
			}.Serialize();
		}
	}
}
