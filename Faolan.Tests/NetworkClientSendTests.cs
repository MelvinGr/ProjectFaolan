using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// A send to a connection that has gone is dropped, not thrown. Fan-outs (a despawn, the tick's regen, a chat line)
	// reach every viewer the world still lists; a throw from one whose socket just closed would skip the rest of the
	// fan-out, and on a disconnect the leaver's own teardown with it.
	public class NetworkClientSendTests
	{
		[Fact]
		public async Task SendAfterStop_IsDropped_AndTheDisconnectRunsOnce()
		{
			using var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
			var accepted = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);

			var disconnects = 0;
			var client = new ChatNetworkClient(accepted, NullLogger.Instance, new ChatPacketTracer(NullLogger.Instance))
			{
				Disconnected = _ =>
				{
					disconnects++;
					return Task.CompletedTask;
				}
			};

			client.Stop();
			await client.SendAsync([0x00, 0x01, 0x00, 0x00], TestContext.Current.CancellationToken);
			client.Stop();

			Assert.Equal(1, disconnects);
		}
	}
}
