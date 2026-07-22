using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Conan;
using Faolan.GameServer.Session;
using Xunit;

namespace Faolan.Tests
{
	// Verifies the phase-4 pilot (Gitea #406): KeepAliveService now builds an Rpc and hands it to client.SendAsync(Rpc)
	// instead of a hand-framed PacketStream. Drives the REAL handlers through a capturing client and asserts the wire
	// bytes are identical to what the old WriteHeader path produced - so the structured send boundary is proven
	// byte-preserving on a live handler, not just on the serializer in isolation.
	public class KeepAliveRpcPilotTests
	{
		private const uint Instance = 0x3BC035EC;

		[Fact]
		public async Task HandlePing_ProducesTheSameFrameAsTheWriteHeaderPath()
		{
			var client = ClientAt(Instance);

			await KeepAliveService.HandlePing(client);

			// The pre-Rpc frame: GameAgent -> GameInterface, GameServerStats, body [0x42B32A07][0][0].
			var expected = new PacketStream();
			expected.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameAgentType, 0x08), Endpoints.EndpointBytes(Endpoints.GameInterfaceType, Instance),
				(ushort)GameInterfaceOpcodes.DispatchGameServerStats);
			expected.WriteUInt32(0x42B32A07);
			expected.WriteUInt32(0);
			expected.WriteUInt32(0);
			expected.WriteLengthHash();

			Assert.Equal(expected.ToArray(), client.Sent);
		}

		[Fact]
		public async Task HandleLatencyCheck_FramesPongWithEchoedCounterAndFlag()
		{
			const uint counter = 0x00000007;
			var client = ClientAt(Instance);

			// Stimulus: an inbound LatencyCheck whose body starts with the counter the reply must echo.
			var incoming = new ConanPacket(BuildFrame(Endpoints.GameCharAgentType, 0x08, Endpoints.GameCharInterfaceType, Instance,
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
			var packet = new PacketStream();
			packet.WriteHeader(Endpoints.EndpointBytes(senderType, senderInstance), Endpoints.EndpointBytes(receiverType, receiverInstance), opcode,
				headerData.Length > 0
					? headerData
					: null);
			packet.WriteArray(body);
			packet.WriteLengthHash();
			return packet.ToArray();
		}

		// Minimal INetworkClient that records the last-sent bytes. The default SendAsync(Rpc) on the interface
		// serializes and routes through SendAsync(byte[]), which we capture.
		private sealed class CapturingClient : INetworkClient
		{
			public byte[]? Sent { get; private set; }

			public INetworkClientSession Session { get; } = new NetworkClientSession();
			public string? IpAddress => "127.0.0.1";

			public void Send(byte[] value)
			{
				Sent = value;
			}

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
