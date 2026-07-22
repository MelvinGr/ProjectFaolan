using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Session;
using Xunit;

namespace Faolan.Tests
{
	// Verifies KeepAliveService (Gitea #406): it builds an Rpc and hands it to client.SendAsync(Rpc). Drives the REAL
	// handlers through a capturing client and asserts the wire bytes match the expected frame - so the structured send
	// boundary is proven byte-preserving on a live handler, not just on the serializer in isolation.
	public class KeepAliveRpcPilotTests
	{
		private const uint Instance = 0x3BC035EC;

		[Fact]
		public async Task HandlePing_ProducesTheExpectedFrame()
		{
			var client = ClientAt(Instance);

			await KeepAliveService.HandlePing(client);

			// The expected frame: GameAgent -> GameInterface, GameServerStats, body [0x42B32A07][0][0].
			var expected = PacketSerializer.Serialize(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameInterfaceType, Instance),
				GameInterfaceOpcodes.DispatchGameServerStats, body => body.WriteUInt32(0x42B32A07).WriteUInt32(0).WriteUInt32(0)));

			Assert.Equal(expected, client.Sent);
		}

		[Fact]
		public async Task HandleLatencyCheck_FramesPongWithEchoedCounterAndFlag()
		{
			const uint counter = 0x00000007;
			var client = ClientAt(Instance);

			// Stimulus: an inbound LatencyCheck whose body starts with the counter the reply must echo. Parsed straight
			// into the Rpc the receive path now dispatches on (Gitea #407).
			var incoming = PacketParser.Parse(BuildFrame(Endpoints.GameCharAgentType, 0x08, Endpoints.GameCharInterfaceType, Instance,
				(ushort)GameCharInterfaceOpcodes.DispatchPong, [], [0x00, 0x00, 0x00, 0x07]));

			await KeepAliveService.HandleLatencyCheck(client, incoming);

			// The tick (3rd body word) is time-based, so assert structure + the deterministic fields, and that the
			// parser's independent crc recompute accepts the frame.
			var pong = new ConanPacket(client.Sent!);
			Assert.True(pong.IsValid);
			Assert.Equal(Endpoints.GameCharAgentType, pong.Sender.Type);
			Assert.Equal(Endpoints.GameCharInterfaceType, pong.Receiver.Type);
			Assert.Equal(Instance, pong.Receiver.Instance);
			Assert.Equal((ushort)GameCharInterfaceOpcodes.DispatchPong, pong.Opcode);
			Assert.Equal(new byte[] { 0x01 }, pong.HeaderData);
			pong.Body.Position = 0;
			Assert.Equal(counter, pong.Body.ReadUInt32());
			Assert.Equal(0x00000012u, pong.Body.ReadUInt32());
		}

		private static CapturingClient ClientAt(uint characterId)
		{
			var client = new CapturingClient();
			client.Session.Character = new Character { Id = characterId };
			return client;
		}

		private static byte[] BuildFrame(uint senderType, uint senderInstance, uint receiverType, uint receiverInstance, ushort opcode, byte[] headerData, byte[] body)
		{
			return PacketSerializer.Serialize(new Rpc(Endpoints.Endpoint(senderType, senderInstance), Endpoints.Endpoint(receiverType, receiverInstance), opcode, headerData, body));
		}

		// Minimal INetworkClient that records the last-sent bytes. The default SendAsync(Rpc) on the interface
		// serializes and routes through SendAsync(byte[]), which we capture.
		private sealed class CapturingClient : INetworkClient
		{
			public byte[]? Sent { get; private set; }

			public INetworkClientSession Session { get; } = new NetworkClientSession();
			public string? IpAddress => "127.0.0.1";

			public Task SendAsync(byte[] value, CancellationToken cancellationToken = default)
			{
				Sent = value;
				return Task.CompletedTask;
			}

			public void Start()
			{
			}

			public void Stop()
			{
			}
		}
	}
}
